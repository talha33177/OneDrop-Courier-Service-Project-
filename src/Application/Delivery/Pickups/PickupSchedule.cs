using Microsoft.EntityFrameworkCore;
using Application.Abstractions;
using Application.Common;
using Domain.Delivery;
using Domain.Parcels;

namespace Application.Delivery.Pickups;

/// <summary>
/// Keeps the courier's daily pickup lists. The courier collects once a day: a parcel booked before the day's cut-off
/// goes with that day's pickup and one booked later with the next day's, so booking a parcel puts its pickup point on
/// the list of its day, and nobody asks for a pickup. <see cref="CatchUpAsync"/>, every few minutes, carries a pickup
/// whose day passed to today, lists the points whose parcels were left behind, and calls off a closed list's pickups
/// with nothing to collect.
/// </summary>
public class PickupSchedule(IAppDbContext db, ITenantContext tenantContext, TimeProvider time)
{
    public const string RecurringId = "pickups";

    /// <summary>Puts the pickup points of <paramref name="parcels"/>, just booked, on the list of the day each goes with.</summary>
    public async Task ScheduleAsync(IReadOnlyCollection<Parcel> parcels, CancellationToken cancellationToken = default)
    {
        var tenant = tenantContext.Require();
        var points = parcels
            .Where(p => p.Status == ParcelStatus.Pending)
            .GroupBy(p => (p.PickupPointId, Day: tenant.PickupDayFor(p.Created)))
            .Select(group => group.First());
        foreach (var parcel in points)
        {
            var day = tenant.PickupDayFor(parcel.Created);
            var listed = await db.PickupRequests.AnyAsync(
                r => r.PickupPointId == parcel.PickupPointId && r.PickupDate == day && r.Status != PickupStatus.Cancelled,
                cancellationToken);
            if (listed)
            {
                continue;
            }

            var pickup = PickupRequest.Schedule(parcel.MerchantId, parcel.PickupPointId, parcel.PickupHubId, day);
            db.PickupRequests.Add(pickup);
            try
            {
                await db.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateException)
            {
                // Another booking at the same point listed it a moment ago (one open pickup per point and day)
                db.Entry(pickup).State = EntityState.Detached;
            }
        }
    }

    /// <summary>
    /// Brings the lists up to date: an open pickup whose day passed goes on today's list (or is called off when the
    /// point is on it already), a point whose parcels are due and not yet listed today is listed, and once today's
    /// list is closed its pickups with nothing left to collect are called off. Running it again changes nothing.
    /// </summary>
    public async Task CatchUpAsync(CancellationToken cancellationToken = default)
    {
        var tenant = tenantContext.Require();
        var now = time.GetUtcNow().UtcDateTime;
        var today = tenant.Today(now);
        var cutoff = tenant.PickupCutoffUtc(today);

        var listed = (await db.PickupRequests
                .Where(r => r.PickupDate == today && r.Status != PickupStatus.Cancelled)
                .Select(r => r.PickupPointId)
                .ToListAsync(cancellationToken))
            .ToHashSet();
        var passed = await db.PickupRequests
            .Where(r => (r.Status == PickupStatus.Scheduled || r.Status == PickupStatus.Assigned) && r.PickupDate < today)
            .OrderByDescending(r => r.PickupDate)
            .ToListAsync(cancellationToken);
        foreach (var pickup in passed)
        {
            var moved = listed.Add(pickup.PickupPointId) ? pickup.CarryTo(today) : pickup.Cancel();
            if (moved.IsFailure)
            {
                throw new InvalidOperationException(moved.Error!.Message);
            }
        }

        await db.SaveChangesAsync(cancellationToken);

        var due = await db.Parcels
            .Where(p => p.Status == ParcelStatus.Pending && p.Created < cutoff)
            .GroupBy(p => p.PickupPointId)
            .Select(g => new { PickupPointId = g.Key, MerchantId = g.Min(p => p.MerchantId), HubId = g.Min(p => p.PickupHubId) })
            .ToListAsync(cancellationToken);
        var unlisted = due.Where(point => listed.Add(point.PickupPointId)).ToList();
        var added = unlisted.Select(point => PickupRequest.Schedule(point.MerchantId, point.PickupPointId, point.HubId, today)).ToList();
        db.PickupRequests.AddRange(added);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // A booking listed one of these points a moment ago: list the others one by one
            added.ForEach(pickup => db.Entry(pickup).State = EntityState.Detached);
            foreach (var point in unlisted)
            {
                var pickup = PickupRequest.Schedule(point.MerchantId, point.PickupPointId, point.HubId, today);
                db.PickupRequests.Add(pickup);
                try
                {
                    await db.SaveChangesAsync(cancellationToken);
                }
                catch (DbUpdateException)
                {
                    db.Entry(pickup).State = EntityState.Detached;
                }
            }
        }

        if (now >= cutoff)
        {
            var empty = await db.PickupRequests
                .Where(r => r.PickupDate == today &&
                    (r.Status == PickupStatus.Scheduled || r.Status == PickupStatus.Assigned) &&
                    !db.Parcels.Any(p => p.PickupPointId == r.PickupPointId && p.Status == ParcelStatus.Pending && p.Created < cutoff))
                .ToListAsync(cancellationToken);
            foreach (var pickup in empty)
            {
                pickup.Cancel();
            }
        }

        await db.SaveChangesAsync(cancellationToken);
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
