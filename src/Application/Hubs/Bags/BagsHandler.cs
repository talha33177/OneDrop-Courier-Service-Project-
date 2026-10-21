using Microsoft.EntityFrameworkCore;
using Application.Abstractions;
using Application.Common;
using Domain.Common;
using Domain.Delivery;
using Domain.Parcels;

namespace Application.Hubs.Bags;

/// <summary>
/// A parcel in a bag as the bag pages show it. <c>Goes</c> is false for a parcel in an open bag that changed since it
/// went in (sent back to its merchant, say) and must come out before the bag is sealed.
/// </summary>
public sealed record BagLine(
    string TrackingCode,
    string Merchant,
    string RecipientName,
    string Area,
    int WeightGrams,
    decimal CodAmount,
    ParcelStatus Status,
    ParcelIssue? Issue,
    DateTime? ReceivedOn,
    bool Missing,
    bool Goes);

/// <summary>A bag as the lists show it, with who sealed and closed it. Times are the courier's.</summary>
public sealed record BagRow(
    string Number,
    string FromHubCode,
    string FromHub,
    string ToHubCode,
    string ToHub,
    BagStatus Status,
    int Parcels,
    int Received,
    int Missing,
    int WeightGrams,
    string? Vehicle,
    DateTime Opened,
    DateTime? SealedOn,
    string? SealedBy,
    DateTime? ArrivedOn,
    DateTime? ClosedOn,
    string? ClosedBy);

/// <summary>A bag with its parcels: on the bags page while it is open or being checked, and as its manifest.</summary>
public sealed record BagView(BagRow Bag, IReadOnlyList<BagLine> Parcels);

/// <summary>Parcels at a hub to send to one other hub that are not in a bag yet.</summary>
public sealed record UnbaggedGroup(string HubCode, string HubName, int Parcels);

/// <summary>
/// A hub's bags: the open ones line-haul is filling, what still waits to go in a bag, the bags on their way here or
/// being checked, and the bags of the last two weeks.
/// </summary>
public sealed record HubBags(
    IReadOnlyList<BagView> Open,
    IReadOnlyList<UnbaggedGroup> Unbagged,
    IReadOnlyList<BagView> Incoming,
    IReadOnlyList<BagRow> Recent);

/// <summary>
/// Bags between hubs. Line-haul fills the open bag for each hub on the scanner (<see cref="HubScan.HubScanHandler"/>),
/// takes a parcel out here, and seals a bag onto the truck, which sends every parcel in it. The hub a bag is for scans
/// it and its parcels in, and closes it once it has looked: every parcel not scanned in is flagged exceptional with the
/// bag, the hubs and the people who sealed and closed it. A hub's staff see the bags from and to their hub.
/// </summary>
public class BagsHandler(
    IAppDbContext db,
    ITenantContext tenantContext,
    ICurrentUser currentUser,
    IUserAccounts accounts,
    HubDirectory hubs,
    TimeProvider time)
{
    public static readonly Error NotFound = Error.NotFound("bag.notFound", "That bag was not found at this hub.");

    /// <summary>How long a hub keeps seeing its bags once they are sealed and gone, or closed.</summary>
    public const int RecentDays = 14;

    public async Task<HubBags?> HubAsync(string hubCode, CancellationToken cancellationToken = default)
    {
        var hub = await hubs.FindAsync(hubCode, cancellationToken);
        if (hub is null)
        {
            return null;
        }

        var since = time.GetUtcNow().UtcDateTime.AddDays(-RecentDays);
        var rows = await RowsAsync(
            // Another hub's bag for here shows once it is sealed; a closed one for two weeks
            db.Bags.Where(b => (b.FromHubId == hub.Id || (b.ToHubId == hub.Id && b.Status != BagStatus.Open)) &&
                (b.Status != BagStatus.Closed || b.ClosedOn >= since)),
            cancellationToken);
        var open = rows.Where(b => b.Status == BagStatus.Open && b.FromHubCode == hub.Code).ToList();
        var incoming = rows.Where(b => b.Status is BagStatus.Sealed or BagStatus.Arrived && b.ToHubCode == hub.Code).ToList();
        var lines = await LinesAsync([.. open.Concat(incoming).Select(b => b.Number)], cancellationToken);

        // What waits here for another hub and is in no open bag yet: line-haul's work still to do
        var inOpenBags =
            from line in db.BagParcels
            join bag in db.Bags on line.BagId equals bag.Id
            where bag.Status == BagStatus.Open && bag.FromHubId == hub.Id
            select line.ParcelId;
        var unbagged = await db.Parcels
            .Where(p => p.CurrentHubId == hub.Id && !inOpenBags.Contains(p.Id) &&
                ((p.Status == ParcelStatus.AtHub && p.DeliveryHubId != hub.Id) ||
                    (p.Status == ParcelStatus.Returning && p.PickupHubId != hub.Id)))
            .GroupBy(p => p.Status == ParcelStatus.Returning ? p.PickupHubId : p.DeliveryHubId)
            .Select(g => new { HubId = g.Key, Count = g.Count() })
            .ToListAsync(cancellationToken);
        var names = await db.Hubs.ToDictionaryAsync(h => h.Id, h => (h.Code, h.Name), cancellationToken);

        return new HubBags(
            [.. open.Select(b => new BagView(b, lines[b.Number]))],
            [.. unbagged.Select(g => new UnbaggedGroup(names[g.HubId].Code, names[g.HubId].Name, g.Count)).OrderBy(g => g.HubName)],
            [.. incoming.Select(b => new BagView(b, lines[b.Number]))],
            [.. rows.Except(open).Except(incoming)]);
    }

    /// <summary>One bag with its parcels, for its manifest; null when there is no such bag.</summary>
    public async Task<BagView?> BagAsync(string number, CancellationToken cancellationToken = default)
    {
        var code = number.Trim().ToUpperInvariant();
        var bag = (await RowsAsync(db.Bags.Where(b => b.Number == code), cancellationToken)).SingleOrDefault();

        return bag is null ? null : new BagView(bag, (await LinesAsync([code], cancellationToken))[code]);
    }

    /// <summary>Line-haul sealed the open bag <paramref name="number"/> at the hub and loaded it on <paramref name="vehicle"/>.</summary>
    public async Task<Result> SealAsync(string hubCode, string number, string? vehicle, CancellationToken cancellationToken = default)
    {
        var (bag, hubId) = await FindAsync(hubCode, number, cancellationToken);
        if (bag is null || bag.FromHubId != hubId)
        {
            return NotFound;
        }

        var parcels = await ParcelsOfAsync(bag, cancellationToken);
        var sealedBag = bag.Seal(parcels, vehicle, currentUser.UserId, time.GetUtcNow().UtcDateTime);

        return sealedBag.IsFailure ? sealedBag : await SaveAsync(cancellationToken);
    }

    /// <summary>Line-haul took <paramref name="trackingCode"/> out of the open bag before sealing it.</summary>
    public async Task<Result> TakeOutAsync(string hubCode, string number, string trackingCode, CancellationToken cancellationToken = default)
    {
        var (bag, hubId) = await FindAsync(hubCode, number, cancellationToken);
        if (bag is null || bag.FromHubId != hubId)
        {
            return NotFound;
        }

        var code = trackingCode.Trim().ToUpperInvariant();
        var parcel = await db.Parcels.SingleOrDefaultAsync(p => p.TrackingCode == code, cancellationToken);
        if (parcel is null)
        {
            return Error.NotFound("bag.parcel", $"{code} was not found.");
        }

        var taken = bag.TakeOut(parcel);

        return taken.IsFailure ? taken : await SaveAsync(cancellationToken);
    }

    /// <summary>
    /// The hub the bag was for has scanned in what it found and closes the bag: every parcel not scanned in is flagged
    /// exceptional as missing, naming who sealed the bag and who closed it. Returns how many were missing.
    /// </summary>
    public async Task<Result<int>> CloseAsync(string hubCode, string number, CancellationToken cancellationToken = default)
    {
        var (bag, hubId) = await FindAsync(hubCode, number, cancellationToken);
        if (bag is null || bag.ToHubId != hubId)
        {
            return NotFound;
        }

        var missingIds = bag.Parcels.Where(line => line.ReceivedOn is null).Select(line => line.ParcelId).ToList();
        var missing = await db.Parcels.Where(p => missingIds.Contains(p.Id)).ToListAsync(cancellationToken);
        var names = await accounts.NamesAsync(
            [.. new[] { bag.SealedById, currentUser.UserId }.OfType<long>()],
            cancellationToken);
        var hubNames = await db.Hubs
            .Where(h => h.Id == bag.FromHubId || h.Id == bag.ToHubId)
            .ToDictionaryAsync(h => h.Id, h => h.Name, cancellationToken);
        var sealer = bag.SealedById is { } sealedBy && names.TryGetValue(sealedBy, out var s) ? s : "someone";
        var closer = currentUser.UserId is { } me && names.TryGetValue(me, out var c) ? c : "someone";
        var who = $"sealed at {hubNames[bag.FromHubId]} by {sealer}, not found at {hubNames[bag.ToHubId]} by {closer}";

        var closed = bag.Close(hubId, missing, who, currentUser.UserId, time.GetUtcNow().UtcDateTime);
        if (closed.IsFailure)
        {
            return closed.Error!;
        }

        var saved = await SaveAsync(cancellationToken);

        return saved.IsFailure ? saved.Error! : missing.Count;
    }

    /// <summary>
    /// The bags as rows, newest first, with the hubs, counts, weight and who sealed and closed each; times turned into
    /// the courier's. Filter <paramref name="bags"/> before calling.
    /// </summary>
    private async Task<IReadOnlyList<BagRow>> RowsAsync(IQueryable<Bag> bags, CancellationToken cancellationToken)
    {
        var tenant = tenantContext.Require();
        var rows = await (
            from bag in bags
            join fromHub in db.Hubs on bag.FromHubId equals fromHub.Id
            join toHub in db.Hubs on bag.ToHubId equals toHub.Id
            orderby bag.Id descending
            select new
            {
                bag.SealedById,
                bag.ClosedById,
                Row = new BagRow(
                    bag.Number,
                    fromHub.Code,
                    fromHub.Name,
                    toHub.Code,
                    toHub.Name,
                    bag.Status,
                    db.BagParcels.Count(l => l.BagId == bag.Id),
                    db.BagParcels.Count(l => l.BagId == bag.Id && l.ReceivedOn != null),
                    db.BagParcels.Count(l => l.BagId == bag.Id && l.Missing),
                    (from line in db.BagParcels
                     join parcel in db.Parcels on line.ParcelId equals parcel.Id
                     where line.BagId == bag.Id
                     select (int?)(parcel.MeasuredWeightGrams ?? parcel.WeightGrams)).Sum() ?? 0,
                    bag.Vehicle,
                    bag.Created,
                    bag.SealedOn,
                    null,
                    bag.ArrivedOn,
                    bag.ClosedOn,
                    null)
            })
            .AsNoTracking()
            .ToListAsync(cancellationToken);
        var names = await accounts.NamesAsync(
            [.. rows.SelectMany(r => new[] { r.SealedById, r.ClosedById }).OfType<long>().Distinct()],
            cancellationToken);

        return
        [
            .. rows.Select(r => r.Row with
            {
                Opened = tenant.Local(r.Row.Opened),
                SealedOn = r.Row.SealedOn is { } sealedOn ? tenant.Local(sealedOn) : null,
                SealedBy = r.SealedById is { } s ? names.GetValueOrDefault(s) : null,
                ArrivedOn = r.Row.ArrivedOn is { } arrived ? tenant.Local(arrived) : null,
                ClosedOn = r.Row.ClosedOn is { } closedOn ? tenant.Local(closedOn) : null,
                ClosedBy = r.ClosedById is { } c ? names.GetValueOrDefault(c) : null
            })
        ];
    }

    /// <summary>The parcels of each bag in <paramref name="numbers"/>, and whether each still goes with an open bag.</summary>
    private async Task<IReadOnlyDictionary<string, IReadOnlyList<BagLine>>> LinesAsync(
        IReadOnlyCollection<string> numbers,
        CancellationToken cancellationToken)
    {
        var tenant = tenantContext.Require();
        var rows = await (
            from line in db.BagParcels
            join bag in db.Bags on line.BagId equals bag.Id
            join parcel in db.Parcels on line.ParcelId equals parcel.Id
            join merchant in db.Merchants on parcel.MerchantId equals merchant.Id
            join area in db.Areas on parcel.AreaId equals area.Id
            where numbers.Contains(bag.Number)
            orderby area.Name, parcel.TrackingCode
            select new
            {
                bag.Number,
                bag.Status,
                bag.FromHubId,
                bag.ToHubId,
                parcel.CurrentHubId,
                OnwardHubId = parcel.Status == ParcelStatus.Returning ? parcel.PickupHubId : parcel.DeliveryHubId,
                Line = new BagLine(
                    parcel.TrackingCode,
                    merchant.Name,
                    parcel.RecipientName,
                    area.Name,
                    parcel.MeasuredWeightGrams ?? parcel.WeightGrams,
                    parcel.CodAmount,
                    parcel.Status,
                    parcel.Issue,
                    line.ReceivedOn,
                    line.Missing,
                    true)
            })
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        return numbers.ToDictionary(
            number => number,
            number => (IReadOnlyList<BagLine>)
            [
                .. rows.Where(row => row.Number == number).Select(row => row.Line with
                {
                    ReceivedOn = row.Line.ReceivedOn is { } received ? tenant.Local(received) : null,
                    Goes = row.Status != BagStatus.Open || (row.CurrentHubId == row.FromHubId && row.OnwardHubId == row.ToHubId &&
                        row.Line.Status is ParcelStatus.AtHub or ParcelStatus.Returning)
                })
            ]);
    }

    private async Task<(Bag? Bag, long HubId)> FindAsync(string hubCode, string number, CancellationToken cancellationToken)
    {
        var hub = await hubs.FindAsync(hubCode, cancellationToken);
        var code = number.Trim().ToUpperInvariant();
        var bag = hub is null
            ? null
            : await db.Bags.Include(b => b.Parcels).SingleOrDefaultAsync(b => b.Number == code, cancellationToken);

        return (bag, hub?.Id ?? 0);
    }

    private async Task<List<Parcel>> ParcelsOfAsync(Bag bag, CancellationToken cancellationToken)
    {
        var ids = bag.Parcels.Select(line => line.ParcelId).ToList();

        return await db.Parcels.Where(p => ids.Contains(p.Id)).ToListAsync(cancellationToken);
    }

    private async Task<Result> SaveAsync(CancellationToken cancellationToken)
    {
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return Error.Conflict("bag.changed", "This bag or its parcels changed just now. Reload the page and try again.");
        }

        return Result.Success();
    }
}
