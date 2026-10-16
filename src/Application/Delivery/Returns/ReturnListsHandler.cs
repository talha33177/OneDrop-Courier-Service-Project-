using Microsoft.EntityFrameworkCore;
using Application.Abstractions;
using Application.Common;
using Application.Delivery.Capacities;
using Application.Hubs;
using Domain.Common;
using Domain.Delivery;
using Domain.Parcels;
using Domain.Payments;

namespace Application.Delivery.Returns;

/// <summary>A returning parcel as the return pages show it: why it came back, and any problem flagged on it.</summary>
public sealed record ReturnParcel(
    string TrackingCode,
    string RecipientName,
    string Area,
    decimal CodAmount,
    string? Reason,
    ParcelIssue? Issue);

/// <summary>Returning parcels waiting at a hub to go back to one pickup point of one merchant.</summary>
public sealed record ReturnGroup(string Merchant, string PickupPoint, string Address, string Phone, IReadOnlyList<ReturnParcel> Parcels);

/// <summary>A rider a hub can send a return list with.</summary>
public sealed record ReturnRider(long Id, string Name, string Phone, Vehicle Vehicle);

/// <summary>A return list as the lists show it. Times are the tenant's.</summary>
public sealed record ReturnListRow(
    string Number,
    string Merchant,
    string PickupPoint,
    string Address,
    string Phone,
    string Rider,
    string RiderPhone,
    ReturnListStatus Status,
    int Parcels,
    DateTime Sent,
    DateTime? HandedOverOn,
    DateTime? ConfirmedOn,
    string? Note);

/// <summary>A return list with its parcels, for its own page.</summary>
public sealed record ReturnListView(ReturnListRow List, IReadOnlyList<ReturnParcel> Parcels);

/// <summary>A hub's returns: what waits to go back to its merchants, the riders to send it with, and its recent lists.</summary>
public sealed record HubReturns(IReadOnlyList<ReturnGroup> Waiting, IReadOnlyList<ReturnRider> Riders, IReadOnlyList<ReturnListRow> Lists);

/// <summary>
/// Return lists at the hub and with the rider. The hub that collected returning parcels sends them back to their
/// merchant with one of its riders; the rider hands them over, which returns each parcel and writes its delivery and
/// return charges to the merchant's ledger in the same save, or says why they could not. Hub staff see their own hub's
/// lists and a rider only their own.
/// </summary>
public class ReturnListsHandler(
    IAppDbContext db,
    ITenantContext tenantContext,
    ICurrentUser currentUser,
    HubDirectory hubs,
    RiderLoads loads,
    TimeProvider time)
{
    public static readonly Error NotFound = Error.NotFound("returnList.notFound", "That return list was not found.");

    /// <summary>How long a hub keeps seeing the lists that are finished.</summary>
    public const int RecentDays = 14;

    public async Task<HubReturns?> HubAsync(string hubCode, CancellationToken cancellationToken = default)
    {
        var hub = await hubs.FindAsync(hubCode, cancellationToken);
        if (hub is null)
        {
            return null;
        }

        var waiting = await (
            from parcel in db.Parcels
            join merchant in db.Merchants on parcel.MerchantId equals merchant.Id
            join point in db.PickupPoints on parcel.PickupPointId equals point.Id
            join area in db.Areas on parcel.AreaId equals area.Id
            where parcel.Status == ParcelStatus.Returning && parcel.CurrentHubId == hub.Id && parcel.PickupHubId == hub.Id
            orderby merchant.Name, point.Name, parcel.Id
            select new
            {
                parcel.PickupPointId,
                Merchant = merchant.Name,
                Point = point.Name,
                point.Address,
                point.ContactPhone,
                Row = new ReturnParcel(parcel.TrackingCode, parcel.RecipientName, area.Name, parcel.CodAmount, parcel.ReturnReason, parcel.Issue)
            })
            .AsNoTracking()
            .ToListAsync(cancellationToken);
        var riders = await db.Riders
            .Where(r => r.HubId == hub.Id && !r.Archived)
            .OrderBy(r => r.Name)
            .Select(r => new ReturnRider(r.Id, r.Name, r.Phone, r.Vehicle))
            .AsNoTracking()
            .ToListAsync(cancellationToken);
        var since = time.GetUtcNow().UtcDateTime.AddDays(-RecentDays);
        var lists = await RowsAsync(
            db.ReturnLists.Where(l => l.HubId == hub.Id &&
                (l.Status == ReturnListStatus.Out || l.Status == ReturnListStatus.HandedOver || l.Created >= since)),
            cancellationToken);

        return new HubReturns(
            [
                .. waiting
                    .GroupBy(p => p.PickupPointId)
                    .Select(g => new ReturnGroup(g.First().Merchant, g.First().Point, g.First().Address, g.First().ContactPhone, [.. g.Select(p => p.Row)]))
            ],
            riders,
            lists);
    }

    /// <summary>The hub sends the parcels <paramref name="trackingCodes"/> back with one of its riders; returns the list's number.</summary>
    public async Task<Result<string>> SendAsync(
        string hubCode,
        long riderId,
        IReadOnlyCollection<string> trackingCodes,
        CancellationToken cancellationToken = default)
    {
        var hub = await hubs.FindAsync(hubCode, cancellationToken);
        if (hub is null)
        {
            return Hubs.HubScan.HubScanHandler.UnknownHub;
        }

        var rider = await db.Riders.SingleOrDefaultAsync(r => r.Id == riderId && r.HubId == hub.Id && !r.Archived, cancellationToken);
        if (rider is null)
        {
            return Error.Validation("returnList.rider", "Choose an active rider of this hub.");
        }

        var codes = trackingCodes.Select(c => c.Trim().ToUpperInvariant()).Where(c => c.Length > 0).Distinct().ToList();
        var parcels = await db.Parcels.Where(p => codes.Contains(p.TrackingCode)).OrderBy(p => p.Id).ToListAsync(cancellationToken);
        if (codes.Except(parcels.Select(p => p.TrackingCode)).FirstOrDefault() is { } unknown)
        {
            return Error.Validation("returnList.unknown", $"{unknown} was not found.");
        }

        // A return list is carried like any other load: the rider's vehicle takes it on top of what they hold already
        var fleet = new Fleet(await db.VehicleCapacities.AsNoTracking().ToListAsync(cancellationToken));
        var load = await loads.CarriedAsync(rider.Id, cancellationToken: cancellationToken);
        foreach (var parcel in parcels)
        {
            var fits = fleet.Take(rider.Vehicle, load, parcel.TrackingCode, parcel.BilledWeightGrams);
            if (fits.IsFailure)
            {
                return Error.Conflict(fits.Error!.Code, $"{fits.Error.Message} Put fewer parcels on this list, or choose another rider.");
            }

            load = load.With(parcel.BilledWeightGrams);
        }

        var sent = ReturnList.Send(rider, hub.Id, parcels);
        if (sent.IsFailure)
        {
            return sent.Error!;
        }

        db.ReturnLists.Add(sent.Value);
        var saved = await SaveAsync(cancellationToken);

        return saved.IsFailure ? saved.Error! : sent.Value.Number;
    }

    /// <summary>The signed-in rider's return lists still to hand over, with their parcels.</summary>
    public async Task<IReadOnlyList<ReturnListView>> RiderListsAsync(CancellationToken cancellationToken = default)
    {
        var rider = await RiderAsync(cancellationToken);
        if (rider is null)
        {
            return [];
        }

        var lists = await RowsAsync(db.ReturnLists.Where(l => l.RiderId == rider.Id && l.Status == ReturnListStatus.Out), cancellationToken);
        var parcels = await ParcelsAsync([.. lists.Select(list => list.Number)], cancellationToken);

        return [.. lists.Select(list => new ReturnListView(list, parcels[list.Number]))];
    }

    /// <summary>One of the signed-in rider's lists still to hand over; another rider's is not found.</summary>
    public async Task<ReturnListView?> RiderListAsync(string number, CancellationToken cancellationToken = default)
    {
        var rider = await RiderAsync(cancellationToken);
        var code = number.Trim().ToUpperInvariant();
        var list = rider is null
            ? null
            : (await RowsAsync(db.ReturnLists.Where(l => l.RiderId == rider.Id && l.Number == code), cancellationToken)).SingleOrDefault();

        return list is null ? null : new ReturnListView(list, (await ParcelsAsync([code], cancellationToken))[code]);
    }

    /// <summary>The rider handed the list's parcels to the merchant: each is returned and charged.</summary>
    public Task<Result> HandOverAsync(string number, CancellationToken cancellationToken = default)
    {
        return RecordAsync(number, (list, parcels, now) => list.HandOver(parcels, now), cancellationToken);
    }

    /// <summary>The rider could not hand the parcels over; they bring them back to the hub.</summary>
    public Task<Result> MissAsync(string number, string? reason, CancellationToken cancellationToken = default)
    {
        return RecordAsync(number, (list, parcels, _) => list.Miss(parcels, reason), cancellationToken);
    }

    /// <summary>
    /// The lists as rows, newest first: merchant, pickup point, rider and parcel count, times turned into the tenant's.
    /// Filter <paramref name="lists"/> before calling; the projection comes last.
    /// </summary>
    internal async Task<IReadOnlyList<ReturnListRow>> RowsAsync(IQueryable<ReturnList> lists, CancellationToken cancellationToken)
    {
        var tenant = tenantContext.Require();
        var rows = await (
            from list in lists
            join merchant in db.Merchants on list.MerchantId equals merchant.Id
            join point in db.PickupPoints on list.PickupPointId equals point.Id
            join rider in db.Riders on list.RiderId equals rider.Id
            orderby list.Id descending
            select new ReturnListRow(
                list.Number,
                merchant.Name,
                point.Name,
                point.Address,
                point.ContactPhone,
                rider.Name,
                rider.Phone,
                list.Status,
                db.ReturnListParcels.Count(p => p.ReturnListId == list.Id),
                list.Created,
                list.HandedOverOn,
                list.ConfirmedOn,
                list.Note))
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        return
        [
            .. rows.Select(row => row with
            {
                Sent = tenant.Local(row.Sent),
                HandedOverOn = row.HandedOverOn is { } handed ? tenant.Local(handed) : null,
                ConfirmedOn = row.ConfirmedOn is { } confirmed ? tenant.Local(confirmed) : null
            })
        ];
    }

    /// <summary>The parcels of each list in <paramref name="numbers"/>; a number with none has an empty list.</summary>
    internal async Task<IReadOnlyDictionary<string, IReadOnlyList<ReturnParcel>>> ParcelsAsync(
        IReadOnlyCollection<string> numbers,
        CancellationToken cancellationToken)
    {
        var rows = await (
            from line in db.ReturnListParcels
            join list in db.ReturnLists on line.ReturnListId equals list.Id
            join parcel in db.Parcels on line.ParcelId equals parcel.Id
            join area in db.Areas on parcel.AreaId equals area.Id
            where numbers.Contains(list.Number)
            orderby parcel.Id
            select new
            {
                list.Number,
                Row = new ReturnParcel(parcel.TrackingCode, parcel.RecipientName, area.Name, parcel.CodAmount, parcel.ReturnReason, parcel.Issue)
            })
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        return numbers.ToDictionary(
            number => number,
            number => (IReadOnlyList<ReturnParcel>)[.. rows.Where(row => row.Number == number).Select(row => row.Row)]);
    }

    private async Task<Result> RecordAsync(
        string number,
        Func<ReturnList, IReadOnlyList<Parcel>, DateTime, Result> change,
        CancellationToken cancellationToken)
    {
        var tenant = tenantContext.Require();
        var rider = await RiderAsync(cancellationToken);
        var code = number.Trim().ToUpperInvariant();
        var list = rider is null
            ? null
            : await db.ReturnLists.Include(l => l.Parcels).SingleOrDefaultAsync(l => l.RiderId == rider.Id && l.Number == code, cancellationToken);
        if (list is null)
        {
            return NotFound;
        }

        var ids = list.Parcels.Select(line => line.ParcelId).ToList();
        var parcels = await db.Parcels.Where(p => ids.Contains(p.Id)).ToListAsync(cancellationToken);
        var now = time.GetUtcNow().UtcDateTime;
        var changed = change(list, parcels, now);
        if (changed.IsFailure)
        {
            return changed;
        }

        foreach (var parcel in parcels.Where(p => p.Status == ParcelStatus.Returned))
        {
            db.LedgerEntries.AddRange(LedgerEntry.For(parcel, tenant.Today(now)));
        }

        return await SaveAsync(cancellationToken);
    }

    private async Task<Result> SaveAsync(CancellationToken cancellationToken)
    {
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return Error.Conflict("returnList.changed", "These parcels changed just now. Reload the page and try again.");
        }

        return Result.Success();
    }

    private Task<Rider?> RiderAsync(CancellationToken cancellationToken)
    {
        var userId = currentUser.UserId;

        return userId is null
            ? Task.FromResult<Rider?>(null)
            : db.Riders.SingleOrDefaultAsync(r => r.UserId == userId && !r.Archived, cancellationToken);
    }
}
