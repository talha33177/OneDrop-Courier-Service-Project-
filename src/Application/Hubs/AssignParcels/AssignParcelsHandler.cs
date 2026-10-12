using Microsoft.EntityFrameworkCore;
using Application.Abstractions;
using Application.Common;
using Domain.Common;
using Domain.Delivery;
using Domain.Network;
using Domain.Pricing;

namespace Application.Hubs.AssignParcels;

/// <summary>
/// A rider of the hub, what they ride and what they carry now: the parcels with them and their weight, against the
/// most their vehicle takes at once (null when the courier has set no limit for it).
/// </summary>
public sealed record RiderLoad(
    long Id,
    string Name,
    string Phone,
    Vehicle Vehicle,
    int WithThem,
    int CarriedGrams,
    int DeliveredToday,
    int? MaxParcels,
    int? MaxLoadGrams);

/// <summary>
/// What an assignment did: the parcels handed over, the ones that could not be with why, and the rider's run, so the
/// hub can print its sheet straight away.
/// </summary>
public sealed record AssignResult(int Assigned, IReadOnlyList<string> Problems, long RunId);

/// <summary>
/// Hub staff hand parcels waiting at their hub to a rider for delivery. The parcels go on the rider's run sheet for the
/// day, opened with the first parcel; each becomes out for delivery. A rider is never handed more than their vehicle
/// carries at once: the parcels due soonest go first, and the ones that do not fit stay at the hub. A parcel that
/// cannot go (not here, delivered from another hub, too heavy for the vehicle) is listed with the reason and the others
/// still go.
/// </summary>
public class AssignParcelsHandler(IAppDbContext db, ITenantContext tenantContext, HubDirectory hubs, TimeProvider time)
{
    public static readonly Error UnknownRider = Error.Validation("assign.rider", "Choose an active rider of this hub.");

    public async Task<IReadOnlyList<RiderLoad>> RidersAsync(string hubCode, CancellationToken cancellationToken = default)
    {
        var tenant = tenantContext.Require();
        var hub = await hubs.FindAsync(hubCode, cancellationToken);
        if (hub is null)
        {
            return [];
        }

        var todayStart = tenant.StartUtc(tenant.Today(time.GetUtcNow().UtcDateTime));
        var riders = await db.Riders
            .Where(r => r.HubId == hub.Id && !r.Archived)
            .OrderBy(r => r.Name)
            .Select(r => new
            {
                r.Id,
                r.Name,
                r.Phone,
                r.Vehicle,
                WithThem = db.Parcels.Count(p => p.RiderId == r.Id),
                Grams = db.Parcels.Where(p => p.RiderId == r.Id).Sum(p => (int?)p.WeightGrams) ?? 0,
                Delivered = db.DeliveryAttempts.Count(a => a.RiderId == r.Id && a.CompletedOn >= todayStart &&
                    (a.Outcome == AttemptOutcome.Delivered || a.Outcome == AttemptOutcome.PartlyDelivered))
            })
            .AsNoTracking()
            .ToListAsync(cancellationToken);
        var fleet = new Fleet(await db.VehicleCapacities.AsNoTracking().ToListAsync(cancellationToken));

        return
        [
            .. riders.Select(r => new RiderLoad(
                r.Id,
                r.Name,
                r.Phone,
                r.Vehicle,
                r.WithThem,
                r.Grams,
                r.Delivered,
                fleet[r.Vehicle]?.MaxParcels,
                fleet[r.Vehicle]?.MaxLoadGrams))
        ];
    }

    public async Task<Result<AssignResult>> AssignAsync(
        string hubCode,
        long riderId,
        IReadOnlyCollection<string> trackingCodes,
        CancellationToken cancellationToken = default)
    {
        var tenant = tenantContext.Require();
        var hub = await hubs.FindAsync(hubCode, cancellationToken);
        if (hub is null)
        {
            return Hubs.HubScan.HubScanHandler.UnknownHub;
        }

        var rider = await db.Riders.SingleOrDefaultAsync(r => r.Id == riderId && r.HubId == hub.Id && !r.Archived, cancellationToken);
        if (rider is null)
        {
            return UnknownRider;
        }

        var codes = trackingCodes.Select(c => c.Trim().ToUpperInvariant()).Where(c => c.Length > 0).Distinct().ToList();
        if (codes.Count == 0)
        {
            return Error.Validation("assign.none", "Choose at least one parcel to hand to the rider.");
        }

        var now = time.GetUtcNow().UtcDateTime;
        var run = await RunAsync(rider, hub, tenant.Today(now), cancellationToken);
        if (run.Status != RunStatus.Open)
        {
            return Error.Conflict("assign.runClosed", $"{rider.Name}'s run for today is already closed.");
        }

        var fleet = new Fleet(await db.VehicleCapacities.AsNoTracking().ToListAsync(cancellationToken));
        var load = Load.Of(await db.Parcels.Where(p => p.RiderId == rider.Id).Select(p => p.WeightGrams).ToListAsync(cancellationToken));

        // Express and food that spoils go first, then the parcels due soonest, so the ones left behind when the vehicle
        // is full are the least urgent
        var parcels = await db.Parcels
            .Where(p => codes.Contains(p.TrackingCode))
            .OrderByDescending(p => p.Speed == DeliverySpeed.Express || p.ProductType == ProductType.FreshFood ||
                p.ProductType == ProductType.Frozen)
            .ThenBy(p => p.DueOn == null)
            .ThenBy(p => p.DueOn)
            .ThenBy(p => p.Id)
            .ToListAsync(cancellationToken);
        var problems = codes.Except(parcels.Select(p => p.TrackingCode)).Select(code => $"{code} was not found.").ToList();
        List<string> full = [];
        var assigned = 0;
        foreach (var parcel in parcels)
        {
            var fits = fleet.Take(rider.Vehicle, load, parcel.TrackingCode, parcel.WeightGrams);
            if (fits.Error?.Code is "capacity.parcels" or "capacity.load")
            {
                full.Add(parcel.TrackingCode);
                continue;
            }

            var handed = fits.IsSuccess ? parcel.AssignTo(rider.Id, hub.Id) : fits;
            if (handed.IsFailure)
            {
                problems.Add(handed.Error!.Message);
                continue;
            }

            db.DeliveryAttempts.Add(run.Add(parcel, now).Value);
            load = load.With(parcel.WeightGrams);
            assigned++;
        }

        if (full.Count > 0)
        {
            var capacity = fleet[rider.Vehicle]!;
            problems.Add(
                $"{string.Join(", ", full.Take(3))}{(full.Count > 3 ? $" and {full.Count - 3} more" : "")} did not fit: " +
                $"{rider.Name}'s {rider.Vehicle.DisplayName().ToLowerInvariant()} takes {capacity.MaxParcels} parcels or " +
                $"{Weight.Kg(capacity.MaxLoadGrams)} at once. Give {(full.Count == 1 ? "it" : "them")} to another rider.");
        }

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            return Error.Conflict("assign.changed", "Some of these parcels were moved by someone else just now. Reload and try again.");
        }

        return new AssignResult(assigned, problems, run.Id);
    }

    /// <summary>The rider's run sheet for the day, opened (and saved, for its id) with the first parcel.</summary>
    private async Task<DeliveryRun> RunAsync(Rider rider, Hub hub, DateOnly today, CancellationToken cancellationToken)
    {
        var run = await db.DeliveryRuns.SingleOrDefaultAsync(r => r.RiderId == rider.Id && r.RunDate == today, cancellationToken);
        if (run is not null)
        {
            return run;
        }

        run = DeliveryRun.Open(rider, today);
        db.DeliveryRuns.Add(run);
        try
        {
            await db.SaveChangesAsync(cancellationToken);

            return run;
        }
        catch (DbUpdateException)
        {
            // Another assignment opened the run at the same moment: use that one
            db.Entry(run).State = EntityState.Detached;

            return await db.DeliveryRuns.SingleAsync(r => r.RiderId == rider.Id && r.RunDate == today, cancellationToken);
        }
    }
}
