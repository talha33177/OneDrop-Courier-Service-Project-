using Microsoft.EntityFrameworkCore;
using Application.Abstractions;
using Application.Common;
using Application.Delivery.Pickups;
using Application.Pricing.DeliveryOptions;
using Domain.Common;
using Domain.Delivery;
using Domain.Parcels;
using Domain.Pricing;

namespace Application.Parcels.Browse;

public sealed record ParcelEventView(DateTime When, ParcelStatus Status, string Note, string? Hub);

public sealed record AttemptView(
    DateTime Assigned,
    string Rider,
    string RiderPhone,
    AttemptOutcome? Outcome,
    decimal Collected,
    string? Reason,
    DateTime? Completed);

/// <summary>Everything about one parcel, for its page. Times are the tenant's.</summary>
public sealed record ParcelView
{
    public required string TrackingCode { get; init; }

    public string? MerchantReference { get; init; }

    public required string Merchant { get; init; }

    public required string MerchantPhone { get; init; }

    public required string RecipientName { get; init; }

    public required string RecipientPhone { get; init; }

    public required string RecipientAddress { get; init; }

    /// <summary>The door pinned on a map, when the merchant pinned it.</summary>
    public GeoPoint? Pin { get; init; }

    public required long AreaId { get; init; }

    public required string Area { get; init; }

    public required string City { get; init; }

    public required string PickupPoint { get; init; }

    public required string PickupAddress { get; init; }

    public required string PickupHub { get; init; }

    public required string DeliveryHub { get; init; }

    public string? ItemDescription { get; init; }

    /// <summary>What the merchant said it weighs.</summary>
    public int WeightGrams { get; init; }

    /// <summary>What a hub's scale said, when one has weighed it.</summary>
    public int? MeasuredWeightGrams { get; init; }

    /// <summary>The weight the charge is worked out on.</summary>
    public int BilledWeightGrams => MeasuredWeightGrams ?? WeightGrams;

    public string? Note { get; init; }

    public ParcelStatus Status { get; init; }

    /// <summary>Where the parcel is now, in words: "At Mirpur hub", "With rider Rafiq Hasan (01722000001)".</summary>
    public string? Location { get; init; }

    public decimal CodAmount { get; init; }

    public decimal? CollectedAmount { get; init; }

    /// <summary>What the recipient paid online before the door; null while it is collected in cash.</summary>
    public decimal? PaidOnline { get; init; }

    public ServiceArea ServiceArea { get; init; }

    /// <summary>The weight's charge plus <see cref="SpeedCharge"/> and <see cref="ProductCharge"/>.</summary>
    public decimal DeliveryCharge { get; init; }

    /// <summary>The speed's code (what the edit form posts back) and its name.</summary>
    public string Speed { get; init; } = "";

    public string SpeedName { get; init; } = "";

    /// <summary>What is inside: its code and name.</summary>
    public string ProductType { get; init; } = "";

    public string ProductName { get; init; } = "";

    public DeliveryTags Tags { get; init; } = new(null, "", null, "", null, false);

    public decimal SpeedCharge { get; init; }

    public decimal ProductCharge { get; init; }

    /// <summary>While it waits for pickup: the day the courier collects it.</summary>
    public DateOnly? PickupOn { get; init; }

    public decimal CodChargePercent { get; init; }

    public decimal CodCharge { get; init; }

    public decimal ReturnCharge { get; init; }

    public decimal TotalCharge { get; init; }

    public int Attempts { get; init; }

    public int MaxAttempts { get; init; }

    public string? HoldReason { get; init; }

    public DateOnly? HoldUntil { get; init; }

    /// <summary>Days after pickup the parcel was promised in; null for no promise.</summary>
    public int? DeliveryDays { get; init; }

    /// <summary>The day it should be delivered by, once picked up.</summary>
    public DateOnly? DueOn { get; init; }

    /// <summary>Still on its way after <see cref="DueOn"/>.</summary>
    public bool Late { get; init; }

    public string? ReturnReason { get; init; }

    /// <summary>A problem a hub flagged for the courier to look at, if any, with its words and when it was flagged.</summary>
    public ParcelIssue? Issue { get; init; }

    public string? IssueNote { get; init; }

    public DateTime? IssueRaisedOn { get; init; }

    public DateTime Booked { get; init; }

    public DateTime? Closed { get; init; }

    /// <summary>The payout (invoice) that paid the parcel's lines out, once it has.</summary>
    public string? PayoutNumber { get; init; }

    public IReadOnlyList<ParcelEventView> Events { get; init; } = [];

    public IReadOnlyList<AttemptView> DeliveryAttempts { get; init; } = [];

    public bool CanEdit => Status == ParcelStatus.Pending;

    public bool CanCancel => Status == ParcelStatus.Pending;

    public bool CanRequestReturn =>
        Status is ParcelStatus.PickedUp or ParcelStatus.AtHub or ParcelStatus.InTransit or ParcelStatus.OnHold;

    /// <summary>A hub can put it on the scale while the courier still has it to deliver.</summary>
    public bool CanReweigh => ParcelStatuses.ToDeliver.Contains(Status);

    /// <summary>A hub can flag a problem while the courier has the parcel.</summary>
    public bool CanFlag => ParcelStatuses.InProgress.Contains(Status);

    /// <summary>The merchant can ask the courier to cancel it or change its cash while it is on its way.</summary>
    public bool CanAsk => ParcelStatuses.ToDeliver.Contains(Status);
}

/// <summary>
/// One parcel by its tracking code, with its tracking history and delivery attempts. A merchant finds only its own
/// parcels (the merchant filter): another merchant's is not found, as an unknown code is.
/// </summary>
public class ParcelDetailsHandler(IAppDbContext db, ITenantContext tenantContext, PickupSchedule schedule, TimeProvider time)
{
    public static readonly Error NotFound = Error.NotFound("parcel.notFound", "No parcel with that tracking code was found.");

    public async Task<Result<ParcelView>> GetAsync(string? trackingCode, CancellationToken cancellationToken = default)
    {
        var tenant = tenantContext.Require();
        var today = tenant.Today(time.GetUtcNow().UtcDateTime);
        var code = trackingCode?.Trim().ToUpperInvariant();
        var found = await (
            from parcel in db.Parcels
            join merchant in db.Merchants on parcel.MerchantId equals merchant.Id
            join area in db.Areas on parcel.AreaId equals area.Id
            join zone in db.Zones on area.ZoneId equals zone.Id
            join point in db.PickupPoints on parcel.PickupPointId equals point.Id
            join pickupHub in db.Hubs on parcel.PickupHubId equals pickupHub.Id
            join deliveryHub in db.Hubs on parcel.DeliveryHubId equals deliveryHub.Id
            where parcel.TrackingCode == code
            select new
            {
                Parcel = parcel,
                Speed = parcel.Speed!,
                Contents = parcel.ProductType!,
                Merchant = merchant.Name,
                MerchantPhone = merchant.ContactPhone,
                Area = area.Name,
                zone.City,
                PickupPoint = point.Name,
                PickupAddress = point.Address,
                PickupHub = pickupHub.Name,
                DeliveryHub = deliveryHub.Name,
                Here = db.Hubs.Where(h => h.Id == parcel.CurrentHubId).Select(h => h.Name).FirstOrDefault(),
                Bound = db.Hubs.Where(h => h.Id == parcel.TransferToHubId).Select(h => h.Name).FirstOrDefault(),
                Rider = db.Riders.Where(r => r.Id == parcel.RiderId).Select(r => new { r.Name, r.Phone }).FirstOrDefault()
            })
            .AsNoTracking()
            .FirstOrDefaultAsync(cancellationToken);
        if (found is null)
        {
            return NotFound;
        }

        var p = found.Parcel;
        var events = await (
            from e in db.ParcelEvents
            where e.ParcelId == p.Id
            orderby e.Id
            select new ParcelEventView(
                e.Created,
                e.Status,
                e.Note,
                db.Hubs.Where(h => h.Id == e.HubId).Select(h => h.Name).FirstOrDefault()))
            .AsNoTracking()
            .ToListAsync(cancellationToken);
        var attempts = await (
            from a in db.DeliveryAttempts
            join rider in db.Riders on a.RiderId equals rider.Id
            where a.ParcelId == p.Id
            orderby a.Id
            select new AttemptView(a.AssignedOn, rider.Name, rider.Phone, a.Outcome, a.CollectedAmount, a.Reason, a.CompletedOn))
            .AsNoTracking()
            .ToListAsync(cancellationToken);
        var payout = await (
            from line in db.LedgerEntries
            join paid in db.Payouts on line.PayoutId equals paid.Id
            where line.ParcelId == p.Id
            select paid.Number)
            .FirstOrDefaultAsync(cancellationToken);

        DateOnly? pickupOn = p.Status == ParcelStatus.Pending ? await schedule.DayForAsync(p.PickupPointId, cancellationToken) : null;

        return new ParcelView
        {
            TrackingCode = p.TrackingCode,
            MerchantReference = p.MerchantReference,
            Merchant = found.Merchant,
            MerchantPhone = found.MerchantPhone,
            RecipientName = p.RecipientName,
            RecipientPhone = p.RecipientPhone,
            RecipientAddress = p.RecipientAddress,
            Pin = p.Pin,
            AreaId = p.AreaId,
            Area = found.Area,
            City = found.City,
            PickupPoint = found.PickupPoint,
            PickupAddress = found.PickupAddress,
            PickupHub = found.PickupHub,
            DeliveryHub = found.DeliveryHub,
            ItemDescription = p.ItemDescription,
            WeightGrams = p.WeightGrams,
            MeasuredWeightGrams = p.MeasuredWeightGrams,
            Note = p.Note,
            Status = p.Status,
            Location = found.Here is not null ? $"At {found.Here}"
                : found.Bound is not null ? $"On the way to {found.Bound}"
                : found.Rider is not null ? $"With rider {found.Rider.Name} ({PhoneNumber.Parse(found.Rider.Phone).Value.Local})"
                : null,
            CodAmount = p.CodAmount,
            CollectedAmount = p.CollectedAmount,
            PaidOnline = p.PaidOnline,
            ServiceArea = p.ServiceArea,
            DeliveryCharge = p.DeliveryCharge,
            Speed = found.Speed.Code,
            SpeedName = found.Speed.Name,
            ProductType = found.Contents.Code,
            ProductName = found.Contents.Name,
            Tags = new DeliveryTags(
                found.Speed.IsDefault ? null : found.Speed.Name,
                found.Speed.Icon,
                found.Contents.IsDefault ? null : found.Contents.Name,
                found.Contents.Icon,
                found.Contents.Handling,
                found.Speed.GoesFirst || found.Contents.GoesFirst),
            SpeedCharge = p.SpeedCharge,
            ProductCharge = p.ProductCharge,
            PickupOn = pickupOn,
            CodChargePercent = p.CodChargePercent,
            CodCharge = p.CodCharge ?? p.Charges.CodChargeOn(p.CodAmount),
            ReturnCharge = p.ReturnCharge,
            TotalCharge = p.TotalCharge,
            Attempts = p.Attempts,
            MaxAttempts = tenant.MaxDeliveryAttempts,
            HoldReason = p.HoldReason,
            HoldUntil = p.HoldUntil,
            DeliveryDays = p.DeliveryDays,
            DueOn = p.DueOn,
            Late = p.IsLate(today),
            ReturnReason = p.ReturnReason,
            Issue = p.Issue,
            IssueNote = p.IssueNote,
            IssueRaisedOn = p.IssueRaisedOn is { } raised ? tenant.Local(raised) : null,
            Booked = tenant.Local(p.Created),
            Closed = p.ClosedOn is { } closed ? tenant.Local(closed) : null,
            PayoutNumber = payout,
            Events = [.. events.Select(e => e with { When = tenant.Local(e.When) })],
            DeliveryAttempts =
            [
                .. attempts.Select(a => a with
                {
                    Assigned = tenant.Local(a.Assigned),
                    Completed = a.Completed is { } done ? tenant.Local(done) : null
                })
            ]
        };
    }
}
