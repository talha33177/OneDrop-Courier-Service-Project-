using Microsoft.EntityFrameworkCore;
using Application.Abstractions;
using Application.Common;
using Application.Delivery.Capacities;
using Application.Delivery.Pickups;
using Application.Pricing.DeliveryOptions;
using Domain.Common;
using Domain.Delivery;
using Domain.Parcels;
using Domain.Payments;

namespace Application.Delivery.RiderDay;

/// <summary>A parcel waiting at a pickup point, for the rider to tick off.</summary>
public sealed record PickupParcel(string TrackingCode, string RecipientName, string Area, decimal CodAmount);

/// <summary>
/// A merchant's door the rider collects from, with it pinned on a map when the merchant pinned it, so the van is
/// guided to it and the stop sits on the round's route.
/// </summary>
public sealed record RiderPickup(
    long Id,
    string Merchant,
    string PickupPoint,
    string Address,
    string Area,
    string ContactPhone,
    DateOnly Date,
    IReadOnlyList<PickupParcel> Parcels,
    decimal? Latitude = null,
    decimal? Longitude = null)
{
    public GeoPoint? Pin => GeoPoint.From(Latitude, Longitude) is { IsSuccess: true } point ? point.Value : null;
}

/// <summary>
/// A parcel the rider has to deliver, with everything needed at the door: how to handle it, and the door pinned on a
/// map when the merchant pinned it.
/// </summary>
public sealed record RiderDelivery(
    string TrackingCode,
    string Merchant,
    string MerchantPhone,
    string RecipientName,
    string RecipientPhone,
    string RecipientAddress,
    string Area,
    decimal CodAmount,
    int WeightGrams,
    string? ItemDescription,
    string? Note,
    int Attempts,
    int MaxAttempts,
    DateOnly? DueOn,
    DeliveryTags Tags,
    decimal? Latitude,
    decimal? Longitude)
{
    public GeoPoint? Pin => GeoPoint.From(Latitude, Longitude) is { IsSuccess: true } point ? point.Value : null;
}

public sealed record RiderDone(string TrackingCode, string RecipientName, string Area, AttemptOutcome Outcome, decimal Collected, string? Reason);

/// <summary>
/// The rider's day on their phone: pickups to make, parcels to deliver, what is done, the cash in hand, what they ride
/// and when they are due back at the hub with the cash (null when the courier sets no time).
/// </summary>
public sealed record RiderToday(
    string Rider,
    string Hub,
    DateOnly Date,
    IReadOnlyList<RiderPickup> Pickups,
    IReadOnlyList<RiderDelivery> Deliveries,
    IReadOnlyList<RiderDone> Done,
    decimal CashInHand,
    Vehicle Vehicle,
    DateTime? DueBack);

/// <summary>
/// What the signed-in rider does: collect at pickup points and record what happened at each door: delivered (with the
/// cash collected), partly delivered, held for another day, or refused. A delivery writes the merchant's ledger lines in
/// the same save. A rider only ever sees and changes their own work.
/// </summary>
public class RiderDayHandler(
    IAppDbContext db,
    ITenantContext tenantContext,
    ICurrentUser currentUser,
    RiderLoads loads,
    PickupSchedule schedule,
    TimeProvider time)
{
    public static readonly Error NotYours = Error.NotFound("rider.parcel", "That parcel is not with you.");

    public async Task<RiderToday?> TodayAsync(CancellationToken cancellationToken = default)
    {
        var tenant = tenantContext.Require();
        var rider = await RiderAsync(cancellationToken);
        if (rider is null)
        {
            return null;
        }

        var today = tenant.Today(time.GetUtcNow().UtcDateTime);
        var hub = await db.Hubs.Where(h => h.Id == rider.HubId).Select(h => h.Name).SingleAsync(cancellationToken);

        var parcels = db.Parcels.AsQueryable();
        var pickups = await (
            from request in db.PickupRequests
            join merchant in db.Merchants on request.MerchantId equals merchant.Id
            join point in db.PickupPoints on request.PickupPointId equals point.Id
            join area in db.Areas on point.AreaId equals area.Id
            where request.RiderId == rider.Id && request.Status == PickupStatus.Assigned && request.PickupDate <= today
            orderby request.PickupDate, request.Id
            select new
            {
                request,
                Merchant = merchant.Name,
                Point = point.Name,
                point.Address,
                Area = area.Name,
                point.ContactPhone,
                point.Latitude,
                point.Longitude
            })
            .AsNoTracking()
            .ToListAsync(cancellationToken);
        var pointIds = pickups.Select(p => p.request.PickupPointId).ToList();
        var waiting = await (
            from parcel in parcels
            join area in db.Areas on parcel.AreaId equals area.Id
            where pointIds.Contains(parcel.PickupPointId) && parcel.Status == ParcelStatus.Pending
            orderby parcel.Id
            select new
            {
                parcel.PickupPointId,
                Row = new PickupParcel(parcel.TrackingCode, parcel.RecipientName, area.Name, parcel.CodAmount)
            })
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        var deliveries = await (
            from attempt in db.DeliveryAttempts
            join parcel in parcels on attempt.ParcelId equals parcel.Id
            join merchant in db.Merchants on parcel.MerchantId equals merchant.Id
            join area in db.Areas on parcel.AreaId equals area.Id
            where attempt.RiderId == rider.Id && attempt.Outcome == null
            orderby parcel.Speed!.GoesFirst || parcel.ProductType!.GoesFirst descending, area.Name, parcel.Id
            select new RiderDelivery(
                parcel.TrackingCode,
                merchant.Name,
                merchant.ContactPhone,
                parcel.RecipientName,
                parcel.RecipientPhone,
                parcel.RecipientAddress,
                area.Name,
                // What is left to collect at the door: nothing once the recipient paid online
                parcel.CodAmount - (parcel.PaidOnline ?? 0),
                parcel.WeightGrams,
                parcel.ItemDescription,
                parcel.Note,
                parcel.Attempts,
                tenant.MaxDeliveryAttempts,
                parcel.DueOn,
                new DeliveryTags(
                    parcel.Speed!.IsDefault ? null : parcel.Speed.Name,
                    parcel.Speed.Icon,
                    parcel.ProductType!.IsDefault ? null : parcel.ProductType.Name,
                    parcel.ProductType.Icon,
                    parcel.ProductType.Handling,
                    parcel.Speed.GoesFirst || parcel.ProductType.GoesFirst),
                parcel.RecipientLatitude,
                parcel.RecipientLongitude))
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        var openRuns = db.DeliveryRuns.Where(r => r.RiderId == rider.Id && (r.Status == RunStatus.Open || r.RunDate == today));
        var done = await (
            from attempt in db.DeliveryAttempts
            join run in openRuns on attempt.RunId equals run.Id
            join parcel in parcels on attempt.ParcelId equals parcel.Id
            join area in db.Areas on parcel.AreaId equals area.Id
            where attempt.Outcome != null
            orderby attempt.CompletedOn descending
            select new RiderDone(parcel.TrackingCode, parcel.RecipientName, area.Name, attempt.Outcome!.Value, attempt.CollectedAmount, attempt.Reason))
            .AsNoTracking()
            .ToListAsync(cancellationToken);
        var cash = await (
            from attempt in db.DeliveryAttempts
            join run in db.DeliveryRuns on attempt.RunId equals run.Id
            where run.RiderId == rider.Id && run.Status == RunStatus.Open
            select (decimal?)attempt.CollectedAmount)
            .SumAsync(cancellationToken) ?? 0;

        return new RiderToday(
            rider.Name,
            hub,
            today,
            [
                .. pickups.Select(p => new RiderPickup(
                    p.request.Id,
                    p.Merchant,
                    p.Point,
                    p.Address,
                    p.Area,
                    p.ContactPhone,
                    p.request.PickupDate,
                    [.. waiting.Where(w => w.PickupPointId == p.request.PickupPointId).Select(w => w.Row)],
                    p.Latitude,
                    p.Longitude))
            ],
            deliveries,
            done,
            cash,
            rider.Vehicle,
            tenant.DueBack(today));
    }

    public Task<Result> DeliverAsync(string trackingCode, decimal collected, string? reason, CancellationToken cancellationToken = default)
    {
        return RecordAsync(
            trackingCode,
            (parcel, now) => parcel.Deliver(collected, reason, now),
            parcel => parcel.Status == ParcelStatus.Delivered ? AttemptOutcome.Delivered : AttemptOutcome.PartlyDelivered,
            // The attempt keeps the rider's cash; the parcel counts what was paid online too
            _ => collected,
            reason,
            cancellationToken);
    }

    public Task<Result> HoldAsync(string trackingCode, string? reason, DateOnly? until, CancellationToken cancellationToken = default)
    {
        var tenant = tenantContext.Require();

        return RecordAsync(
            trackingCode,
            (parcel, now) => parcel.Hold(reason, until, tenant.Today(now), tenant.MaxDeliveryAttempts),
            _ => AttemptOutcome.Hold,
            _ => 0,
            reason,
            cancellationToken);
    }

    public Task<Result> RefuseAsync(string trackingCode, string? reason, CancellationToken cancellationToken = default)
    {
        return RecordAsync(trackingCode, (parcel, _) => parcel.Refuse(reason), _ => AttemptOutcome.Refused, _ => 0, reason, cancellationToken);
    }

    /// <summary>
    /// The rider collected <paramref name="trackingCodes"/> at the pickup point; the rest stay with the merchant. This
    /// is the day's visit to the point: whatever the rider leaves goes with the next day's pickup, which is listed at
    /// once. A parcel booked while the rider stood at the door is theirs to take, so one that reached the point after
    /// the list was drawn (it is not among <paramref name="shownCodes"/>) stops the pickup and is offered again.
    /// </summary>
    public async Task<Result<int>> CompletePickupAsync(
        long requestId,
        IReadOnlyCollection<string> trackingCodes,
        IReadOnlyCollection<string>? shownCodes = null,
        CancellationToken cancellationToken = default)
    {
        var rider = await RiderAsync(cancellationToken);
        var request = rider is null
            ? null
            : await db.PickupRequests.SingleOrDefaultAsync(r => r.Id == requestId && r.RiderId == rider.Id, cancellationToken);
        if (request is null)
        {
            return Pickups.PickupsHandler.NotFound;
        }

        var tenant = tenantContext.Require();
        if (request.PickupDate > tenant.Today(time.GetUtcNow().UtcDateTime))
        {
            return Pickups.PickupsHandler.NotYet;
        }

        var codes = trackingCodes.Select(c => c.Trim().ToUpperInvariant()).ToList();
        var waiting = await db.Parcels
            .Where(p => p.PickupPointId == request.PickupPointId && p.MerchantId == request.MerchantId &&
                p.Status == ParcelStatus.Pending)
            .ToListAsync(cancellationToken);
        var picked = waiting.Where(p => codes.Contains(p.TrackingCode)).ToList();
        if (!rider!.Vehicle.CollectsPickups())
        {
            return Error.Conflict("pickup.vehicle", "Only a pickup van collects from merchants. Ask the hub for a delivery run instead.");
        }

        // Booked while the rider was at the door: the merchant is owed the collection today, so the list is drawn again
        var shown = shownCodes?.Select(c => c.Trim().ToUpperInvariant()).ToHashSet();
        var since = shown is null ? 0 : waiting.Count(p => !shown.Contains(p.TrackingCode));
        if (since > 0)
        {
            return Error.Conflict(
                "pickup.booked",
                $"The merchant booked {since} more parcel{(since == 1 ? "" : "s")} while you were here, now on the list. " +
                "Tick what you are taking and save again.");
        }

        // Whatever the rider puts in the van must fit it together with what it already carries; the rest waits for the next pickup
        var fleet = new Fleet(await db.VehicleCapacities.AsNoTracking().ToListAsync(cancellationToken));
        var load = await loads.CarriedAsync(rider.Id, request.Id, cancellationToken);
        foreach (var parcel in picked.OrderBy(p => p.Id))
        {
            var fits = fleet.Take(rider.Vehicle, load, parcel.TrackingCode, parcel.BilledWeightGrams);
            if (fits.IsFailure)
            {
                return Error.Conflict(
                    fits.Error!.Code,
                    $"{fits.Error.Message} Untick what does not fit: the merchant's other parcels come with the next pickup.");
            }

            load = load.With(parcel.BilledWeightGrams);
        }

        var now = time.GetUtcNow().UtcDateTime;
        var today = tenant.Today(now);
        foreach (var parcel in picked)
        {
            parcel.PickUp(today, rider.Id);
        }

        var completed = request.Complete(picked.Count, now);
        if (completed.IsFailure)
        {
            return completed.Error!;
        }

        await db.SaveChangesAsync(cancellationToken);

        // The van has been here today, so whatever the rider left behind goes with tomorrow's pickup
        var left = await db.Parcels
            .Where(p => p.PickupPointId == request.PickupPointId && p.Status == ParcelStatus.Pending)
            .ToListAsync(cancellationToken);
        await schedule.ScheduleAsync(left, cancellationToken);

        return picked.Count;
    }

    private async Task<Result> RecordAsync(
        string trackingCode,
        Func<Parcel, DateTime, Result> change,
        Func<Parcel, AttemptOutcome> outcome,
        Func<Parcel, decimal> collected,
        string? reason,
        CancellationToken cancellationToken)
    {
        var tenant = tenantContext.Require();
        var rider = await RiderAsync(cancellationToken);
        if (rider is null)
        {
            return NotYours;
        }

        var code = trackingCode.Trim().ToUpperInvariant();
        var found = await (
            from attempt in db.DeliveryAttempts
            join parcel in db.Parcels on attempt.ParcelId equals parcel.Id
            where attempt.RiderId == rider.Id && attempt.Outcome == null && parcel.TrackingCode == code
            select new { Attempt = attempt, Parcel = parcel })
            .SingleOrDefaultAsync(cancellationToken);
        if (found is null)
        {
            return NotYours;
        }

        var now = time.GetUtcNow().UtcDateTime;
        var changed = change(found.Parcel, now);
        if (changed.IsFailure)
        {
            return changed;
        }

        found.Attempt.Complete(outcome(found.Parcel), collected(found.Parcel), reason, now);
        if (found.Parcel.IsFinal)
        {
            db.LedgerEntries.AddRange(LedgerEntry.For(found.Parcel, tenant.Today(now)));
        }

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return Error.Conflict("rider.changed", "This parcel changed at the hub just now. Reload your list.");
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
