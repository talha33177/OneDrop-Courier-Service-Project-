using Microsoft.EntityFrameworkCore;
using Application.Abstractions;
using Application.Common;
using Application.Hubs;
using Domain.Common;
using Domain.Delivery;
using Domain.Parcels;

namespace Application.Delivery.Pickups;

/// <summary>
/// A merchant's pickup point and its parcels waiting to be collected: those booked before today's cut-off go with
/// today's pickup, the rest with the next day's.
/// </summary>
public sealed record PickupPointRow(long Id, string Name, string Address, string Area, bool IsDefault, int Today, int Later);

/// <summary>
/// A pickup as the merchant's and the hub's pages show it: the parcels it collects (those booked at the point before
/// its day's cut-off, or, once done, the ones picked). For the hub, an open pickup also says what they weigh and the
/// vehicle to send (<see cref="Fleet.For"/>).
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

    public Vehicle? Send { get; init; }
}

/// <summary>
/// The courier's daily pickup as a merchant sees it: the cut-off and when the pickup leaves the hub (null when the
/// courier sets none), whether today's list is closed, the day a parcel booked now is collected, each pickup point's
/// waiting parcels and the latest pickups.
/// </summary>
public sealed record MerchantPickups(
    TimeOnly? Cutoff,
    TimeOnly? LeavesAt,
    bool TodayClosed,
    DateOnly NextPickup,
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
    TimeProvider time)
{
    public static readonly Error NotFound = Error.NotFound("pickup.notFound", "That pickup was not found.");

    public async Task<MerchantPickups> ForMerchantAsync(CancellationToken cancellationToken = default)
    {
        var tenant = tenantContext.Require();
        var now = time.GetUtcNow().UtcDateTime;
        var cutoff = tenant.PickupCutoffUtc(tenant.Today(now));
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
                db.Parcels.Count(p => p.PickupPointId == point.Id && p.Status == ParcelStatus.Pending && p.Created < cutoff),
                db.Parcels.Count(p => p.PickupPointId == point.Id && p.Status == ParcelStatus.Pending && p.Created >= cutoff)))
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        return new MerchantPickups(
            tenant.PickupCutoff,
            tenant.PickupTime,
            now >= cutoff,
            tenant.PickupDayFor(now),
            points,
            await RowsAsync(db.PickupRequests.OrderByDescending(r => r.PickupDate).ThenByDescending(r => r.Id).Take(30), cancellationToken));
    }

    /// <summary>
    /// The hub's open pickups (today's, any carried over and tomorrow's so far), each with the parcels it collects and
    /// the vehicle to send, and the ones completed today. Null when there is no such hub.
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
                ? row with { Send = fleet.For(Load.Of(row.Weights)) }
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

        var fleet = new Fleet(await db.VehicleCapacities.AsNoTracking().ToListAsync(cancellationToken));
        var assigned = request.Assign(rider, fleet, Load.Of(await WeightsAsync(request, cancellationToken)));
        if (assigned.IsSuccess)
        {
            await db.SaveChangesAsync(cancellationToken);
        }

        return assigned;
    }

    /// <summary>
    /// Sends <paramref name="riderId"/> on every pickup of <paramref name="day"/> at the hub that has no rider yet: the
    /// pickup truck's round. Returns how many it took, or why it took none.
    /// </summary>
    public async Task<Result<int>> AssignAllAsync(string hubCode, DateOnly day, long riderId, CancellationToken cancellationToken = default)
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

        var waiting = await db.PickupRequests
            .Where(r => r.HubId == hub.Id && r.PickupDate == day && r.Status == PickupStatus.Scheduled)
            .ToListAsync(cancellationToken);
        if (waiting.Count == 0)
        {
            return Error.Conflict("pickup.noneWaiting", "Every pickup of that day already has a rider.");
        }

        var fleet = new Fleet(await db.VehicleCapacities.AsNoTracking().ToListAsync(cancellationToken));
        foreach (var request in waiting)
        {
            var assigned = request.Assign(rider, fleet, Load.Of(await WeightsAsync(request, cancellationToken)));
            if (assigned.IsFailure)
            {
                return assigned.Error!;
            }
        }

        await db.SaveChangesAsync(cancellationToken);

        return waiting.Count;
    }

    /// <summary>The weights of the parcels <paramref name="request"/> collects.</summary>
    private async Task<List<int>> WeightsAsync(PickupRequest request, CancellationToken cancellationToken)
    {
        var cutoff = tenantContext.Require().PickupCutoffUtc(request.PickupDate);

        return await db.Parcels
            .Where(p => p.PickupPointId == request.PickupPointId && p.Status == ParcelStatus.Pending && p.Created < cutoff)
            .Select(p => p.WeightGrams)
            .ToListAsync(cancellationToken);
    }

    private async Task<IReadOnlyList<PickupRow>> RowsAsync(IQueryable<PickupRequest> requests, CancellationToken cancellationToken)
    {
        var tenant = tenantContext.Require();
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
                .Select(p => new { p.PickupPointId, p.Created, p.WeightGrams })
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

                var cutoff = tenant.PickupCutoffUtc(row.Date);
                var weights = waiting[row.PickupPointId].Where(p => p.Created < cutoff).Select(p => p.WeightGrams).ToList();

                return row with { Parcels = weights.Count, BookedGrams = weights.Sum(), Weights = weights };
            })
        ];
    }
}
