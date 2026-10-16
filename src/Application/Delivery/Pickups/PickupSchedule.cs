using Microsoft.EntityFrameworkCore;
using Application.Abstractions;
using Application.Common;
using Domain.Delivery;
using Domain.Parcels;

namespace Application.Delivery.Pickups;

/// <summary>
/// The courier's one visit a day to a pickup point. The truck collects from a point once: what a merchant books before
/// the truck reaches it goes with that day's pickup, however late (the truck leaves the hub at the courier's pickup
/// time and reaches each merchant in turn), and what is booked once the truck has been there goes with the next day's.
/// </summary>
public static class PickupVisits
{
    /// <summary>
    /// The pickups the truck made today: it has been to those points, so what is booked there from now on waits for
    /// tomorrow's pickup. A pickup counts from when the rider recorded it, whichever day it was listed for.
    /// </summary>
    public static IQueryable<PickupRequest> MadeToday(this IQueryable<PickupRequest> pickups, TenantInfo tenant, DateTime utcNow)
    {
        var start = tenant.StartUtc(tenant.Today(utcNow));

        return pickups.Where(r => r.Status == PickupStatus.Completed && r.CompletedOn >= start);
    }
}

/// <summary>
/// Keeps the courier's daily pickup lists. Booking a parcel puts its pickup point on the list of its day, today unless
/// the truck has already been to the point today, and nobody asks for a pickup. <see cref="CatchUpAsync"/>, every few
/// minutes, carries a pickup whose day passed to today, lists the points whose parcels were left behind, and calls off
/// the pickups with nothing left to collect.
/// </summary>
public class PickupSchedule(IAppDbContext db, ITenantContext tenantContext, TimeProvider time)
{
    public const string RecurringId = "pickups";

    /// <summary>
    /// Puts the pickup points of <paramref name="parcels"/>, just booked, on the list of the day each goes with, and
    /// returns that day for each point.
    /// </summary>
    public async Task<IReadOnlyDictionary<long, DateOnly>> ScheduleAsync(
        IReadOnlyCollection<Parcel> parcels,
        CancellationToken cancellationToken = default)
    {
        var tenant = tenantContext.Require();
        var now = time.GetUtcNow().UtcDateTime;
        var today = tenant.Today(now);
        var visited = await VisitedAsync(tenant, now, cancellationToken);
        var days = new Dictionary<long, DateOnly>();
        foreach (var parcel in parcels.Where(p => p.Status == ParcelStatus.Pending).DistinctBy(p => p.PickupPointId))
        {
            var day = visited.Contains(parcel.PickupPointId) ? today.AddDays(1) : today;
            days[parcel.PickupPointId] = day;
            var listed = await db.PickupRequests.AnyAsync(
                r => r.PickupPointId == parcel.PickupPointId && r.PickupDate == day && r.Status != PickupStatus.Cancelled,
                cancellationToken);
            if (!listed)
            {
                await ListAsync(PickupRequest.Schedule(parcel.MerchantId, parcel.PickupPointId, parcel.PickupHubId, day), cancellationToken);
            }
        }

        return days;
    }

    /// <summary>The day a parcel waiting at <paramref name="pointId"/> goes with: today, or tomorrow once the van has been there today.</summary>
    public async Task<DateOnly> DayForAsync(long pointId, CancellationToken cancellationToken = default)
    {
        var tenant = tenantContext.Require();
        var now = time.GetUtcNow().UtcDateTime;
        var visited = await db.PickupRequests.MadeToday(tenant, now).AnyAsync(r => r.PickupPointId == pointId, cancellationToken);

        return visited ? tenant.Today(now).AddDays(1) : tenant.Today(now);
    }

    /// <summary>
    /// Brings the lists up to date: an open pickup whose day passed goes on today's list, or tomorrow's when the van
    /// has been to the point today (or is called off when the point is on that list already), a pickup with nothing
    /// left to collect is called off, and a point with parcels waiting
    /// and no pickup to collect them is listed, for today or, when the truck has been there today, for tomorrow.
    /// Running it again changes nothing.
    /// </summary>
    public async Task CatchUpAsync(CancellationToken cancellationToken = default)
    {
        var tenant = tenantContext.Require();
        var now = time.GetUtcNow().UtcDateTime;
        var today = tenant.Today(now);
        var tomorrow = today.AddDays(1);
        var visited = await VisitedAsync(tenant, now, cancellationToken);

        var listed = (await db.PickupRequests
                .Where(r => r.PickupDate >= today && r.PickupDate <= tomorrow && r.Status != PickupStatus.Cancelled)
                .Select(r => new { r.PickupPointId, r.PickupDate })
                .ToListAsync(cancellationToken))
            .Select(r => (r.PickupPointId, r.PickupDate))
            .ToHashSet();
        var visitedPoints = visited.ToList();

        // Open pickups whose day passed, and any left open today at a point the van has been to (listed by a booking
        // made just as the rider finished): both go to the day the point is next visited
        var passed = await db.PickupRequests
            .Where(r => (r.Status == PickupStatus.Scheduled || r.Status == PickupStatus.Assigned) &&
                (r.PickupDate < today || (r.PickupDate == today && visitedPoints.Contains(r.PickupPointId))))
            .OrderByDescending(r => r.PickupDate)
            .ToListAsync(cancellationToken);
        foreach (var pickup in passed)
        {
            var day = visited.Contains(pickup.PickupPointId) ? tomorrow : today;
            var moved = listed.Add((pickup.PickupPointId, day)) ? pickup.CarryTo(day) : pickup.Cancel();
            if (moved.IsFailure)
            {
                throw new InvalidOperationException(moved.Error!.Message);
            }
        }

        var empty = await db.PickupRequests
            .Where(r => (r.Status == PickupStatus.Scheduled || r.Status == PickupStatus.Assigned) &&
                !db.Parcels.Any(p => p.PickupPointId == r.PickupPointId && p.Status == ParcelStatus.Pending))
            .ToListAsync(cancellationToken);
        foreach (var pickup in empty)
        {
            pickup.Cancel();
        }

        await db.SaveChangesAsync(cancellationToken);

        var waiting = await db.Parcels
            .Where(p => p.Status == ParcelStatus.Pending)
            .GroupBy(p => p.PickupPointId)
            .Select(g => new { PickupPointId = g.Key, MerchantId = g.Min(p => p.MerchantId), HubId = g.Min(p => p.PickupHubId) })
            .ToListAsync(cancellationToken);
        var unlisted = waiting
            .Select(point => PickupRequest.Schedule(
                point.MerchantId,
                point.PickupPointId,
                point.HubId,
                visited.Contains(point.PickupPointId) ? tomorrow : today))
            .Where(pickup => listed.Add((pickup.PickupPointId, pickup.PickupDate)))
            .ToList();
        foreach (var pickup in unlisted)
        {
            await ListAsync(pickup, cancellationToken);
        }
    }

    /// <summary>The points the truck has been to today: what is booked there now goes with tomorrow's pickup.</summary>
    public Task<HashSet<long>> VisitedTodayAsync(CancellationToken cancellationToken = default)
    {
        return VisitedAsync(tenantContext.Require(), time.GetUtcNow().UtcDateTime, cancellationToken);
    }

    private async Task<HashSet<long>> VisitedAsync(TenantInfo tenant, DateTime now, CancellationToken cancellationToken)
    {
        return (await db.PickupRequests
                .MadeToday(tenant, now)
                .Select(r => r.PickupPointId)
                .Distinct()
                .ToListAsync(cancellationToken))
            .ToHashSet();
    }

    /// <summary>Saves a pickup on the list, unless another booking listed the point for that day a moment ago.</summary>
    private async Task ListAsync(PickupRequest pickup, CancellationToken cancellationToken)
    {
        db.PickupRequests.Add(pickup);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // One pickup per point and day: the other booking's stands
            db.Entry(pickup).State = EntityState.Detached;
        }
    }
}

/// <summary>Every few minutes: keeps the day's pickup lists up to date (see <see cref="PickupSchedule.CatchUpAsync"/>).</summary>
public class PickupsJob(PickupSchedule schedule) : ITenantJob
{
    public Task RunAsync(CancellationToken cancellationToken)
    {
        return schedule.CatchUpAsync(cancellationToken);
    }
}
