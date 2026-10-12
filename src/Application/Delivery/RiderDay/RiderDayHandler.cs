using Microsoft.EntityFrameworkCore;
using Application.Abstractions;
using Application.Common;
using Domain.Common;
using Domain.Delivery;
using Domain.Parcels;
using Domain.Payments;
using Domain.Pricing;

namespace Application.Delivery.RiderDay;

/// <summary>A parcel waiting at a pickup point, for the rider to tick off.</summary>
public sealed record PickupParcel(string TrackingCode, string RecipientName, string Area, decimal CodAmount);

public sealed record RiderPickup(
    long Id,
    string Merchant,
    string PickupPoint,
    string Address,
    string Area,
    string ContactPhone,
    DateOnly Date,
    IReadOnlyList<PickupParcel> Parcels);

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
    DeliverySpeed Speed,
    ProductType ProductType,
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
public class RiderDayHandler(IAppDbContext db, ITenantContext tenantContext, ICurrentUser currentUser, TimeProvider time)
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
            where request.RiderId == rider.Id && request.Status == PickupStatus.Assigned
            orderby request.PickupDate, request.Id
            select new { request, Merchant = merchant.Name, Point = point.Name, point.Address, Area = area.Name, point.ContactPhone })
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
                parcel.Created,
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
            orderby parcel.Speed == DeliverySpeed.Express || parcel.ProductType == ProductType.FreshFood ||
                parcel.ProductType == ProductType.Frozen descending, area.Name, parcel.Id
            select new RiderDelivery(
                parcel.TrackingCode,
                merchant.Name,
                merchant.ContactPhone,
                parcel.RecipientName,
                parcel.RecipientPhone,
                parcel.RecipientAddress,
                area.Name,
                parcel.CodAmount,
                parcel.WeightGrams,
                parcel.ItemDescription,
                parcel.Note,
                parcel.Attempts,
                tenant.MaxDeliveryAttempts,
                parcel.DueOn,
                parcel.Speed,
                parcel.ProductType,
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
                    [
                        .. waiting
                            .Where(w => w.PickupPointId == p.request.PickupPointId &&
                                w.Created < tenant.PickupCutoffUtc(p.request.PickupDate))
                            .Select(w => w.Row)
                    ]))
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
            parcel => parcel.CollectedAmount ?? 0,
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
    /// The rider collected <paramref name="trackingCodes"/> at the pickup point; the rest stay with the merchant. Only
    /// parcels booked before the pickup's cut-off go with it: one booked later waits for the next day's pickup.
    /// </summary>
    public async Task<Result<int>> CompletePickupAsync(
        long requestId,
        IReadOnlyCollection<string> trackingCodes,
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
        var cutoff = tenant.PickupCutoffUtc(request.PickupDate);
        var codes = trackingCodes.Select(c => c.Trim().ToUpperInvariant()).ToList();
        var picked = await db.Parcels
            .Where(p => p.PickupPointId == request.PickupPointId && p.MerchantId == request.MerchantId &&
                p.Status == ParcelStatus.Pending && p.Created < cutoff && codes.Contains(p.TrackingCode))
            .ToListAsync(cancellationToken);
        var now = time.GetUtcNow().UtcDateTime;
        var today = tenant.Today(now);
        foreach (var parcel in picked)
        {
            parcel.PickUp(today);
        }

        var completed = request.Complete(picked.Count, now);
        if (completed.IsFailure)
        {
            return completed.Error!;
        }

        await db.SaveChangesAsync(cancellationToken);

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
