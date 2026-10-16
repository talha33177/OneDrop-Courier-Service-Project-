using Microsoft.EntityFrameworkCore;
using Application.Abstractions;
using Domain.Delivery;
using Domain.Parcels;

namespace Application.Delivery.Capacities;

/// <summary>
/// What riders carry now, so that no vehicle is given more than it holds: the parcels with them (out for delivery, going
/// back to a merchant, or collected by the pickup van and not yet scanned in at the hub), counted at the weight the hub's
/// scale read where it has weighed them, and, for a pickup van, the parcels at the points it is on its way to collect
/// from, counted at their booked weights.
/// </summary>
public class RiderLoads(IAppDbContext db)
{
    /// <summary>
    /// The load of each of <paramref name="riderIds"/> (empty for a rider with nothing). The pickup
    /// <paramref name="exceptPickupId"/> is left out, so that giving it to the rider it already has does not count it twice.
    /// </summary>
    public async Task<Dictionary<long, Load>> CarriedAsync(
        IReadOnlyCollection<long> riderIds,
        long? exceptPickupId = null,
        CancellationToken cancellationToken = default)
    {
        var held = await db.Parcels
            .Where(p => p.RiderId != null && riderIds.Contains(p.RiderId.Value))
            .Select(p => new { RiderId = p.RiderId!.Value, Grams = p.MeasuredWeightGrams ?? p.WeightGrams })
            .AsNoTracking()
            .ToListAsync(cancellationToken);
        var pickups = await db.PickupRequests
            .Where(r => r.RiderId != null && riderIds.Contains(r.RiderId.Value) && r.Status == PickupStatus.Assigned &&
                (exceptPickupId == null || r.Id != exceptPickupId))
            .Select(r => new { RiderId = r.RiderId!.Value, r.PickupPointId })
            .AsNoTracking()
            .ToListAsync(cancellationToken);
        var points = pickups.Select(p => p.PickupPointId).Distinct().ToList();
        var waiting = (await db.Parcels
                .Where(p => points.Contains(p.PickupPointId) && p.Status == ParcelStatus.Pending)
                .Select(p => new { p.PickupPointId, p.WeightGrams })
                .AsNoTracking()
                .ToListAsync(cancellationToken))
            .ToLookup(p => p.PickupPointId);

        var loads = riderIds.ToDictionary(id => id, _ => new Load(0, 0, 0));
        foreach (var group in held.GroupBy(p => p.RiderId))
        {
            loads[group.Key] = Load.Of([.. group.Select(p => p.Grams)]);
        }

        // A point on two of a rider's open pickups (a day carried over, and the next) is collected once
        foreach (var point in pickups.GroupBy(p => (p.RiderId, p.PickupPointId)))
        {
            var weights = waiting[point.Key.PickupPointId].Select(p => p.WeightGrams).ToList();
            loads[point.Key.RiderId] = loads[point.Key.RiderId].Plus(Load.Of(weights));
        }

        return loads;
    }

    public async Task<Load> CarriedAsync(long riderId, long? exceptPickupId = null, CancellationToken cancellationToken = default)
    {
        return (await CarriedAsync([riderId], exceptPickupId, cancellationToken))[riderId];
    }
}
