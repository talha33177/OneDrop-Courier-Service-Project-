using Microsoft.EntityFrameworkCore;
using Application.Abstractions;
using Application.Common;
using Domain.Common;
using Domain.Network;
using Domain.Delivery;
using Domain.Parcels;
using Domain.Payments;

namespace Application.Hubs.HubScan;

/// <summary>What a hub scan can do.</summary>
public enum ScanMode
{
    /// <summary>A parcel arrives: from a pickup, off a transfer, or back from a rider.</summary>
    Receive,

    /// <summary>A parcel leaves for another hub: forward to the hub that delivers it, or back to the hub that collected it.</summary>
    Dispatch,

    /// <summary>A returning parcel is handed back to its merchant.</summary>
    HandBack
}

public enum ScanTone
{
    Success,
    Info,
    Error
}

/// <summary>The large answer the scan page shows: what happened and what to do with the parcel next.</summary>
public sealed record ScanAnswer(ScanTone Tone, string Title, string? Detail = null, string? TrackingCode = null);

/// <summary>
/// Hub staff scan a parcel's label at their hub. Each scan says plainly what to do next: assign it to a rider, send it to
/// another hub, or hand it back to the merchant. Scanning the same parcel twice is harmless. Another courier's parcel or
/// hub is not found. Two scans of one parcel at once lose on its row version and are retried from fresh data.
/// </summary>
public class HubScanHandler(IAppDbContext db, ITenantContext tenantContext, HubDirectory hubs, TimeProvider time)
{
    private const int Retries = 3;

    public static readonly Error UnknownHub = Error.NotFound("hub.notFound", "That hub was not found.");

    public async Task<Result<ScanAnswer>> ScanAsync(
        string hubCode,
        ScanMode mode,
        string? trackingCode,
        CancellationToken cancellationToken = default)
    {
        var hub = await hubs.FindAsync(hubCode, cancellationToken);
        if (hub is null)
        {
            return UnknownHub;
        }

        var code = ScanCode.Read(trackingCode);
        if (code is null)
        {
            return new ScanAnswer(ScanTone.Error, "Scan or type a tracking code.");
        }

        for (var attempt = 1; ; attempt++)
        {
            ScanAnswer answer;
            if (code.StartsWith(BagPrefix, StringComparison.Ordinal))
            {
                answer = mode == ScanMode.Receive
                    ? await BagArrivesAsync(code, hub, cancellationToken)
                    : new ScanAnswer(ScanTone.Error, $"{code} is a bag.", "Scan a bag in Receive mode when it reaches the hub that it is for.", code);
            }
            else
            {
                var parcel = await db.Parcels.SingleOrDefaultAsync(p => p.TrackingCode == code, cancellationToken);
                if (parcel is null)
                {
                    return new ScanAnswer(ScanTone.Error, $"{code} was not found.", "Check the label and scan it again.", code);
                }

                answer = mode switch
                {
                    ScanMode.Receive => await ReceiveAsync(parcel, hub, cancellationToken),
                    ScanMode.Dispatch => await BagAsync(parcel, hub, cancellationToken),
                    _ => await HandBackAsync(parcel, hub, cancellationToken)
                };
            }

            try
            {
                await db.SaveChangesAsync(cancellationToken);

                return answer;
            }
            catch (DbUpdateException) when (attempt < Retries)
            {
                // Someone else moved the parcel or opened the same bag meanwhile: forget this attempt and scan again
                // from fresh rows
                foreach (var entry in db.Parcels.Local.Cast<object>()
                    .Concat(db.ParcelEvents.Local)
                    .Concat(db.LedgerEntries.Local)
                    .Concat(db.Bags.Local)
                    .Concat(db.BagParcels.Local)
                    .ToList())
                {
                    db.Entry(entry).State = EntityState.Detached;
                }
            }
        }
    }

    /// <summary>A bag's label starts with this; a tracking code never does.</summary>
    public const string BagPrefix = "BG-";

    /// <summary>The hub scanned a bag off the truck: it is opened here, and the answer says how many parcels to scan in.</summary>
    private async Task<ScanAnswer> BagArrivesAsync(string number, Hub hub, CancellationToken cancellationToken)
    {
        var bag = await db.Bags.Include(b => b.Parcels).SingleOrDefaultAsync(b => b.Number == number, cancellationToken);
        if (bag is null)
        {
            return new ScanAnswer(ScanTone.Error, $"Bag {number} was not found.", "Check the label and scan it again.");
        }

        var arrived = bag.Arrive(hub.Id, time.GetUtcNow().UtcDateTime);
        if (arrived.IsFailure)
        {
            return new ScanAnswer(ScanTone.Error, arrived.Error!.Message);
        }

        var from = await db.Hubs.Where(h => h.Id == bag.FromHubId).Select(h => h.Name).SingleAsync(cancellationToken);
        var inside = bag.Parcels.Count;
        var scanned = bag.Parcels.Count(line => line.ReceivedOn is not null);
        if (arrived.Value == ScanOutcome.AlreadyRecorded)
        {
            return new ScanAnswer(
                ScanTone.Info,
                bag.Status == BagStatus.Closed ? $"Bag {number} is closed" : $"Bag {number} is already open here",
                $"{scanned} of {inside} parcel{(inside == 1 ? "" : "s")} scanned in.");
        }

        return new ScanAnswer(
            ScanTone.Success,
            $"Bag {number} from {from}",
            $"{inside} parcel{(inside == 1 ? "" : "s")} inside. Scan each one in; close the bag on the Bags page when you are done.");
    }

    /// <summary>
    /// Line-haul put the parcel in the open bag for the hub it goes to next, opening one when there is none. A parcel
    /// already in another open bag here (it was sent back to its merchant since) moves to the right one.
    /// </summary>
    private async Task<ScanAnswer> BagAsync(Parcel parcel, Hub hub, CancellationToken cancellationToken)
    {
        var toHubId = parcel.OnwardHubId();
        var to = await db.Hubs.Where(h => h.Id == toHubId).Select(h => h.Name).SingleAsync(cancellationToken);
        if (parcel.TransferToHubId == toHubId)
        {
            return new ScanAnswer(ScanTone.Info, $"Already on its way to {to}", null, parcel.TrackingCode);
        }

        var can = parcel.CanDispatch(hub.Id, toHubId);
        if (can.IsFailure)
        {
            return new ScanAnswer(ScanTone.Error, can.Error!.Message, null, parcel.TrackingCode);
        }

        var openHere = await db.Bags
            .Include(b => b.Parcels)
            .Where(b => b.Status == BagStatus.Open && b.FromHubId == hub.Id &&
                (b.ToHubId == toHubId || b.Parcels.Any(line => line.ParcelId == parcel.Id)))
            .ToListAsync(cancellationToken);
        var moved = false;
        foreach (var other in openHere.Where(b => b.ToHubId != toHubId))
        {
            moved = other.TakeOut(parcel).IsSuccess || moved;
        }

        var bag = openHere.SingleOrDefault(b => b.ToHubId == toHubId);
        if (bag is null)
        {
            bag = Bag.Open(hub.Id, toHubId);
            db.Bags.Add(bag);
        }

        var added = bag.Add(parcel);
        if (added.IsFailure)
        {
            return new ScanAnswer(ScanTone.Error, added.Error!.Message, null, parcel.TrackingCode);
        }

        var count = bag.Parcels.Count;
        var which = bag.IsNew ? "a new bag" : $"bag {bag.Number}";
        if (added.Value == ScanOutcome.AlreadyRecorded)
        {
            return new ScanAnswer(ScanTone.Info, $"Already in the bag for {to}", $"In {which} with {count - 1} other{(count == 2 ? "" : "s")}.", parcel.TrackingCode);
        }

        return new ScanAnswer(
            ScanTone.Success,
            $"Bag for {to}",
            (moved ? "Moved from the bag it was in, which no longer goes its way. " : "") +
            $"Put it in {which} for {to}: {count} parcel{(count == 1 ? "" : "s")} in it now. Seal the bag on the Bags page when the truck is loaded.",
            parcel.TrackingCode);
    }

    private async Task<ScanAnswer> ReceiveAsync(Parcel parcel, Hub hub, CancellationToken cancellationToken)
    {
        var received = parcel.ReceiveAt(hub.Id, tenantContext.Require().Today(time.GetUtcNow().UtcDateTime));
        if (received.IsFailure)
        {
            return new ScanAnswer(ScanTone.Error, received.Error!.Message, null, parcel.TrackingCode);
        }

        var title = received.Value == ScanOutcome.AlreadyRecorded ? "Already scanned in here" : "Received";
        var next = await NextStepAsync(parcel, hub, cancellationToken);
        if (received.Value == ScanOutcome.AlreadyRecorded)
        {
            return new ScanAnswer(ScanTone.Info, title, next, parcel.TrackingCode);
        }

        // Ticked off in the bag it came in; the last parcel in closes the bag
        var bags = await db.Bags
            .Include(b => b.Parcels)
            .Where(b => (b.Status == BagStatus.Sealed || b.Status == BagStatus.Arrived) && b.Parcels.Any(line => line.ParcelId == parcel.Id))
            .ToListAsync(cancellationToken);
        foreach (var bag in bags)
        {
            var closed = bag.Received(parcel.Id, hub.Id, time.GetUtcNow().UtcDateTime);
            if (bag.ToHubId == hub.Id)
            {
                var inside = bag.Parcels.Count;
                next += closed
                    ? $" Bag {bag.Number}: all {inside} parcel{(inside == 1 ? " is" : "s are")} in, and the bag is closed."
                    : $" Bag {bag.Number}: {bag.Parcels.Count(line => line.ReceivedOn is not null)} of {inside} scanned in.";
            }
        }

        return new ScanAnswer(ScanTone.Success, title, next, parcel.TrackingCode);
    }

    private async Task<ScanAnswer> HandBackAsync(Parcel parcel, Hub hub, CancellationToken cancellationToken)
    {
        var tenant = tenantContext.Require();
        var now = time.GetUtcNow().UtcDateTime;
        var returned = parcel.ReturnToMerchant(hub.Id, now);
        if (returned.IsFailure)
        {
            return new ScanAnswer(ScanTone.Error, returned.Error!.Message, null, parcel.TrackingCode);
        }

        var merchant = await db.Merchants.Where(m => m.Id == parcel.MerchantId).Select(m => m.Name).SingleAsync(cancellationToken);
        if (returned.Value == ScanOutcome.AlreadyRecorded)
        {
            return new ScanAnswer(ScanTone.Info, $"Already returned to {merchant}", null, parcel.TrackingCode);
        }

        db.LedgerEntries.AddRange(LedgerEntry.For(parcel, tenant.Today(now)));

        return new ScanAnswer(
            ScanTone.Success,
            $"Returned to {merchant}",
            $"Get the merchant's signature. Charged ৳{parcel.TotalCharge:N0} (delivery and return).",
            parcel.TrackingCode);
    }

    /// <summary>What hub staff do with a parcel that is now at their hub.</summary>
    private async Task<string> NextStepAsync(Parcel parcel, Hub hub, CancellationToken cancellationToken)
    {
        async Task<string> HubName(long id)
        {
            return await db.Hubs.Where(h => h.Id == id).Select(h => h.Name).SingleAsync(cancellationToken);
        }

        switch (parcel.Status)
        {
            case ParcelStatus.Returning when parcel.PickupHubId == hub.Id:
                var merchant = await db.Merchants.Where(m => m.Id == parcel.MerchantId).Select(m => m.Name).SingleAsync(cancellationToken);
                return $"Returning: hand it back to {merchant}.";

            case ParcelStatus.Returning:
                return $"Returning: send it to {await HubName(parcel.PickupHubId)}.";

            case ParcelStatus.OnHold:
                return $"On hold ({parcel.HoldReason})" +
                    (parcel.HoldUntil is { } until ? $" until {until:ddd d MMM}" : "") +
                    ". Assign it to a rider again.";

            case ParcelStatus.AtHub when parcel.DeliveryHubId == hub.Id:
                var area = await db.Areas.Where(a => a.Id == parcel.AreaId).Select(a => a.Name).SingleAsync(cancellationToken);
                return $"Delivered from this hub ({area}): assign it to a rider.";

            default:
                return $"Send it to {await HubName(parcel.DeliveryHubId)}.";
        }
    }
}
