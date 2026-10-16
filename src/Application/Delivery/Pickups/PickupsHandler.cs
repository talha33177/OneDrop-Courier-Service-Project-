using Microsoft.EntityFrameworkCore;
using Application.Abstractions;
using Application.Common;
using Application.Delivery.Capacities;
using Application.Hubs;
using Domain.Common;
using Domain.Delivery;
using Domain.Parcels;

namespace Application.Delivery.Pickups;

/// <summary>
/// A merchant's pickup point and its parcels waiting to be collected, with the day they go: today, or tomorrow once the
/// pickup van has been to the point today.
/// </summary>
public sealed record PickupPointRow(long Id, string Name, string Address, string Area, bool IsDefault, int Waiting, DateOnly GoesOn);

/// <summary>
/// A pickup as the merchant's and the hub's pages show it: the parcels it collects (those waiting at the point, or,
/// once done, the ones picked). For the hub, an open pickup also says what they weigh and how many trips of the pickup
/// van it takes.
/// </summary>
public sealed record PickupRow(
    long Id,
    long PickupPointId,
    string Merchant,
    string MerchantPhone,
    string PickupPoint,
    string Address,
    string Area,
    string ContactPhone,
    DateOnly Date,
    PickupStatus Status,
    long? RiderId,
    string? Rider,
    string? RiderPhone,
    int? Picked)
{
    /// <summary>Parcels waiting at the point that go with this pickup.</summary>
    public int Parcels { get; init; }

    public int BookedGrams { get; init; }

    /// <summary>What each of those parcels weighs, as the merchant booked it.</summary>
    public IReadOnlyList<int> Weights { get; init; } = [];

    /// <summary>How many loads of the pickup van this pickup is: more than one when it is bigger than the whole van.</summary>
    public int Trips { get; init; } = 1;
}

/// <summary>
/// What giving the day's open pickups to one van did: how many it took, and how many stay on the list because the van
/// would be over its load (with the reason), for another van or for after it has unloaded.
/// </summary>
public sealed record PickupRound(int Assigned, int Left, string? Why);

/// <summary>
/// The courier's daily pickup as a merchant sees it: when the pickup leaves the hub (null when the courier sets no
/// time), each pickup point's waiting parcels with the day they go, and the latest pickups.
/// </summary>
public sealed record MerchantPickups(
    TimeOnly? LeavesAt,
    IReadOnlyList<PickupPointRow> Points,
    IReadOnlyList<PickupRow> Pickups);

/// <summary>
/// The daily pickups (scheduled by <see cref="PickupSchedule"/>): a merchant follows its own, and the hub of each
/// point's zone sends a rider to collect.
/// </summary>
public class PickupsHandler(
    IAppDbContext db,
    ITenantContext tenantContext,
    HubDirectory hubs,
    RiderLoads loads,
    TimeProvider time)
{
    public static readonly Error NotFound = Error.NotFound("pickup.notFound", "That pickup was not found.");

    /// <summary>The van visits a point once a day: a pickup dated after today is for a point it has been to already.</summary>
    public static readonly Error NotYet = Error.Conflict(
        "pickup.notYet",
        "The van has already been to this point today. What was booked since goes with the pickup of the next day.");

    public async Task<MerchantPickups> ForMerchantAsync(CancellationToken cancellationToken = default)
    {
        var tenant = tenantContext.Require();
        var now = time.GetUtcNow().UtcDateTime;
        var today = tenant.Today(now);
        var points = await (
            from point in db.PickupPoints
            join area in db.Areas on point.AreaId equals area.Id
            where !point.Archived
            orderby point.IsDefault descending, point.Name
            select new PickupPointRow(
                point.Id,
                point.Name,
                point.Address,
                area.Name,
                point.IsDefault,
                db.Parcels.Count(p => p.PickupPointId == point.Id && p.Status == ParcelStatus.Pending),
                today))
            .AsNoTracking()
            .ToListAsync(cancellationToken);
        var visited = await db.PickupRequests.MadeToday(tenant, now).Select(r => r.PickupPointId).Distinct().ToListAsync(cancellationToken);

        return new MerchantPickups(
            tenant.PickupTime,
            [.. points.Select(point => visited.Contains(point.Id) ? point with { GoesOn = today.AddDays(1) } : point)],
            await RowsAsync(db.PickupRequests.OrderByDescending(r => r.PickupDate).ThenByDescending(r => r.Id).Take(30), cancellationToken));
    }

    /// <summary>
    /// The hub's open pickups (today's, any carried over and tomorrow's so far), each with the parcels it collects and
    /// the trips of the pickup van it takes, and the ones completed today. Null when there is no such hub.
    /// </summary>
    public async Task<IReadOnlyList<PickupRow>?> ForHubAsync(string hubCode, CancellationToken cancellationToken = default)
    {
        var tenant = tenantContext.Require();
        var hub = await hubs.FindAsync(hubCode, cancellationToken);
        if (hub is null)
        {
            return null;
        }

        var todayStart = tenant.StartUtc(tenant.Today(time.GetUtcNow().UtcDateTime));
        var requests = db.PickupRequests
            .Where(r => r.HubId == hub.Id &&
                (r.Status == PickupStatus.Scheduled || r.Status == PickupStatus.Assigned ||
                    (r.Status == PickupStatus.Completed && r.CompletedOn >= todayStart)))
            .OrderBy(r => r.PickupDate)
            .ThenBy(r => r.Status)
            .ThenBy(r => r.Id);
        var rows = await RowsAsync(requests, cancellationToken);
        var fleet = new Fleet(await db.VehicleCapacities.AsNoTracking().ToListAsync(cancellationToken));

        return
        [
            .. rows.Select(row => row.Status is PickupStatus.Scheduled or PickupStatus.Assigned
                ? row with { Trips = fleet.Trips(Vehicle.Van, Load.Of(row.Weights)) }
                : row)
        ];
    }

    public async Task<Result> AssignAsync(string hubCode, long requestId, long riderId, CancellationToken cancellationToken = default)
    {
        var hub = await hubs.FindAsync(hubCode, cancellationToken);
        var request = hub is null
            ? null
            : await db.PickupRequests.SingleOrDefaultAsync(r => r.Id == requestId && r.HubId == hub.Id, cancellationToken);
        if (request is null)
        {
            return NotFound;
        }

        var rider = await db.Riders.SingleOrDefaultAsync(r => r.Id == riderId, cancellationToken);
        if (rider is null)
        {
            return Hubs.AssignParcels.AssignParcelsHandler.UnknownRider;
        }

        // The van visits a point once a day: no second pickup for a point it has been to, nor one dated tomorrow
        var tenant = tenantContext.Require();
        var now = time.GetUtcNow().UtcDateTime;
        if (request.IsOpen && (request.PickupDate > tenant.Today(now) ||
            await db.PickupRequests.MadeToday(tenant, now).AnyAsync(r => r.PickupPointId == request.PickupPointId, cancellationToken)))
        {
            return NotYet;
        }

        var fleet = new Fleet(await db.VehicleCapacities.AsNoTracking().ToListAsync(cancellationToken));
        var carries = await loads.CarriedAsync(rider.Id, request.Id, cancellationToken);
        var assigned = request.Assign(rider, fleet, carries, Load.Of(await WeightsAsync(request, cancellationToken)));
        if (assigned.IsSuccess)
        {
            await db.SaveChangesAsync(cancellationToken);
        }

        return assigned;
    }

    /// <summary>
    /// Sends the pickup van <paramref name="riderId"/> on the pickups of <paramref name="day"/> at the hub that have no
    /// rider yet: the round. The van takes them oldest first for as long as it holds them together with what it
    /// carries already; the rest stay on the list for another van or for a second trip. Returns what it took, or why it
    /// took none.
    /// </summary>
    public async Task<Result<PickupRound>> AssignAllAsync(string hubCode, DateOnly day, long riderId, CancellationToken cancellationToken = default)
    {
        var hub = await hubs.FindAsync(hubCode, cancellationToken);
        if (hub is null)
        {
            return NotFound;
        }

        var rider = await db.Riders.SingleOrDefaultAsync(r => r.Id == riderId, cancellationToken);
        if (rider is null)
        {
            return Hubs.AssignParcels.AssignParcelsHandler.UnknownRider;
        }

        var tenant = tenantContext.Require();
        var now = time.GetUtcNow().UtcDateTime;
        if (day > tenant.Today(now))
        {
            return NotYet;
        }

        var visited = await db.PickupRequests.MadeToday(tenant, now).Select(r => r.PickupPointId).Distinct().ToListAsync(cancellationToken);
        var waiting = await db.PickupRequests
            .Where(r => r.HubId == hub.Id && r.PickupDate == day && r.Status == PickupStatus.Scheduled && !visited.Contains(r.PickupPointId))
            .OrderBy(r => r.Id)
            .ToListAsync(cancellationToken);
        if (waiting.Count == 0)
        {
            return Error.Conflict("pickup.noneWaiting", "Every pickup of that day already has a rider.");
        }

        var fleet = new Fleet(await db.VehicleCapacities.AsNoTracking().ToListAsync(cancellationToken));
        var carries = await loads.CarriedAsync(rider.Id, cancellationToken: cancellationToken);
        var assigned = 0;
        Error? full = null;
        foreach (var request in waiting)
        {
            var load = Load.Of(await WeightsAsync(request, cancellationToken));
            var sent = request.Assign(rider, fleet, carries, load);
            if (sent.IsSuccess)
            {
                carries = carries.Plus(load);
                assigned++;
            }
            else if (sent.Error!.Code == "pickup.full")
            {
                full ??= sent.Error;
            }
            else
            {
                return sent.Error!;
            }
        }

        if (assigned == 0)
        {
            return full!;
        }

        await db.SaveChangesAsync(cancellationToken);

        return new PickupRound(assigned, waiting.Count - assigned, full?.Message);
    }

    /// <summary>The weights of the parcels <paramref name="request"/> collects: those waiting at its point.</summary>
    private async Task<List<int>> WeightsAsync(PickupRequest request, CancellationToken cancellationToken)
    {
        return await db.Parcels
            .Where(p => p.PickupPointId == request.PickupPointId && p.Status == ParcelStatus.Pending)
            .Select(p => p.WeightGrams)
            .ToListAsync(cancellationToken);
    }

    private async Task<IReadOnlyList<PickupRow>> RowsAsync(IQueryable<PickupRequest> requests, CancellationToken cancellationToken)
    {
        var rows = await (
            from request in requests
            join merchant in db.Merchants on request.MerchantId equals merchant.Id
            join point in db.PickupPoints on request.PickupPointId equals point.Id
            join area in db.Areas on point.AreaId equals area.Id
            select new PickupRow(
                request.Id,
                point.Id,
                merchant.Name,
                merchant.ContactPhone,
                point.Name,
                point.Address,
                area.Name,
                point.ContactPhone,
                request.PickupDate,
                request.Status,
                request.RiderId,
                db.Riders.Where(r => r.Id == request.RiderId).Select(r => r.Name).FirstOrDefault(),
                db.Riders.Where(r => r.Id == request.RiderId).Select(r => r.Phone).FirstOrDefault(),
                request.PickedParcels))
            .AsNoTracking()
            .ToListAsync(cancellationToken);
        var open = rows.Where(r => r.Status is PickupStatus.Scheduled or PickupStatus.Assigned).ToList();
        var points = open.Select(r => r.PickupPointId).Distinct().ToList();
        var waiting = (await db.Parcels
                .Where(p => points.Contains(p.PickupPointId) && p.Status == ParcelStatus.Pending)
                .Select(p => new { p.PickupPointId, p.WeightGrams })
                .AsNoTracking()
                .ToListAsync(cancellationToken))
            .ToLookup(p => p.PickupPointId);

        return
        [
            .. rows.Select(row =>
            {
                if (row.Status is not (PickupStatus.Scheduled or PickupStatus.Assigned))
                {
                    return row;
                }

                var weights = waiting[row.PickupPointId].Select(p => p.WeightGrams).ToList();

                return row with { Parcels = weights.Count, BookedGrams = weights.Sum(), Weights = weights };
            })
        ];
    }
}
