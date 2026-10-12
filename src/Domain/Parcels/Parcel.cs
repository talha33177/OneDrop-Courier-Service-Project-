using Domain.Common;
using Domain.Pricing;

namespace Domain.Parcels;

/// <summary>Who receives a parcel and what it is. The area, hubs and charges are resolved by the caller.</summary>
public sealed record ParcelDetails(
    long AreaId,
    long DeliveryHubId,
    string RecipientName,
    PhoneNumber RecipientPhone,
    string RecipientAddress,
    decimal CodAmount,
    int WeightGrams,
    string? ItemDescription,
    string? Note,
    ParcelCharges Charges)
{
    /// <summary>Where the door is, pinned on a map, so the rider can be guided to it; null when not pinned.</summary>
    public GeoPoint? Pin { get; init; }
}

/// <summary>Everything needed to book a parcel.</summary>
public sealed record NewParcel(long MerchantId, long PickupPointId, long PickupHubId, ParcelDetails Details)
{
    /// <summary>The merchant's own order or invoice number.</summary>
    public string? MerchantReference { get; init; }

    public string? IdempotencyKey { get; init; }

    public byte[]? RequestHash { get; init; }
}

/// <summary>
/// One consignment from a merchant to a recipient. It is booked <see cref="ParcelStatus.Pending"/>, collected from the
/// merchant, sorted at hubs, taken out by a rider and delivered, held for another day, or returned. Where it is lives
/// in three fields: <see cref="CurrentHubId"/> while at a hub, <see cref="TransferToHubId"/> while travelling between
/// hubs and <see cref="RiderId"/> while a rider has it; at most one is set. Its charges and delivery time are worked
/// out from the rate card when it is booked and never change after it is picked up; the day it is due
/// (<see cref="DueOn"/>) is counted from the day the courier takes it. It goes regular or express
/// (<see cref="Speed"/>) and says what is inside (<see cref="ProductType"/>), each with its extra in the delivery charge,
/// and may carry the door pinned on a map (<see cref="Pin"/>). A hub can flag a problem on it
/// (<see cref="Issue"/>) for the courier to look at.
/// </summary>
public class Parcel : TenantEntity, IMerchantOwned
{
    public const int MaxWeightGrams = 30_000;
    public const decimal MaxCodAmount = 1_000_000m;

    private readonly List<ParcelEvent> events = [];

    private Parcel()
    {
    }

    public long MerchantId { get; private set; }

    public long PickupPointId { get; private set; }

    /// <summary>The hub that collects the parcel: its pickup point's zone's hub. A return is handed back from here.</summary>
    public long PickupHubId { get; private set; }

    /// <summary>The recipient's area.</summary>
    public long AreaId { get; private set; }

    /// <summary>The hub that delivers the parcel: the recipient's area's zone's hub.</summary>
    public long DeliveryHubId { get; private set; }

    /// <summary>
    /// The public tracking code (OD10000001), assigned by the database sequence on insert. Null until saved: a non-null
    /// value here would be sent to the database instead of letting the sequence fill it.
    /// </summary>
    public string TrackingCode { get; private set; } = null!;

    /// <summary>The merchant's own order or invoice number.</summary>
    public string? MerchantReference { get; private set; }

    /// <summary>The Idempotency-Key header. A retried request with the same key returns this parcel.</summary>
    public string? IdempotencyKey { get; private set; }

    /// <summary>Hash of the request body, so a reused key with a different body is rejected.</summary>
    public byte[]? RequestHash { get; private set; }

    public string RecipientName { get; private set; } = "";

    /// <summary>E.164.</summary>
    public string RecipientPhone { get; private set; } = "";

    public string RecipientAddress { get; private set; } = "";

    /// <summary>The door pinned on a map; both null when the merchant did not pin it.</summary>
    public decimal? RecipientLatitude { get; private set; }

    public decimal? RecipientLongitude { get; private set; }

    public GeoPoint? Pin => GeoPoint.From(RecipientLatitude, RecipientLongitude) is { IsSuccess: true } point ? point.Value : null;

    public string? ItemDescription { get; private set; }

    /// <summary>Regular, or express: sooner, and first on the hub's shelf and the rider's run.</summary>
    public DeliverySpeed Speed { get; private set; }

    /// <summary>What is inside, which says how to handle it (fragile, frozen and so on).</summary>
    public ProductType ProductType { get; private set; }

    /// <summary>The express charge it was booked at, part of <see cref="DeliveryCharge"/>; 0 for regular.</summary>
    public decimal SpeedCharge { get; private set; }

    /// <summary>The extra for what is inside it was booked at, part of <see cref="DeliveryCharge"/>; 0 for general goods.</summary>
    public decimal ProductCharge { get; private set; }

    /// <summary>Goes first on the hub's shelf and the rider's run: express, or food that spoils.</summary>
    public bool IsPriority => DeliveryOptions.IsPriority(Speed, ProductType);

    /// <summary>What the merchant said the parcel weighs when booking it.</summary>
    public int WeightGrams { get; private set; }

    /// <summary>What a hub's scale said, once one has weighed it. The charge follows this when it is set.</summary>
    public int? MeasuredWeightGrams { get; private set; }

    /// <summary>The weight the parcel is charged on: the hub's measurement when there is one, else the merchant's word.</summary>
    public int BilledWeightGrams => MeasuredWeightGrams ?? WeightGrams;

    /// <summary>Cash the rider collects for the merchant. Zero when the recipient has already paid.</summary>
    public decimal CodAmount { get; private set; }

    /// <summary>Instructions for the rider, such as "call before coming".</summary>
    public string? Note { get; private set; }

    public ServiceArea ServiceArea { get; private set; }

    public decimal DeliveryCharge { get; private set; }

    public decimal CodChargePercent { get; private set; }

    public decimal ReturnCharge { get; private set; }

    /// <summary>Days after pickup the courier promised to deliver in, from the rate card. Null: no promise.</summary>
    public int? DeliveryDays { get; private set; }

    /// <summary>
    /// The day the parcel should be delivered by: the day the courier took it plus <see cref="DeliveryDays"/>, or the
    /// later day the recipient asked for. Null before pickup and when no time was promised.
    /// </summary>
    public DateOnly? DueOn { get; private set; }

    public ParcelStatus Status { get; private set; }

    public long? CurrentHubId { get; private set; }

    public long? TransferToHubId { get; private set; }

    public long? RiderId { get; private set; }

    /// <summary>Delivery attempts made: each time a rider recorded what happened at the door.</summary>
    public int Attempts { get; private set; }

    public string? HoldReason { get; private set; }

    /// <summary>The day the recipient asked to be delivered on, when they gave one.</summary>
    public DateOnly? HoldUntil { get; private set; }

    /// <summary>Why the parcel is going back to the merchant.</summary>
    public string? ReturnReason { get; private set; }

    /// <summary>A problem a hub flagged for the courier to look at; null while there is none.</summary>
    public ParcelIssue? Issue { get; private set; }

    /// <summary>What the problem is, in the words of the hub that flagged it.</summary>
    public string? IssueNote { get; private set; }

    /// <summary>When the problem was first flagged (UTC).</summary>
    public DateTime? IssueRaisedOn { get; private set; }

    /// <summary>The cash collected at the door; set once delivered.</summary>
    public decimal? CollectedAmount { get; private set; }

    /// <summary>The COD charge on <see cref="CollectedAmount"/>; set once delivered.</summary>
    public decimal? CodCharge { get; private set; }

    /// <summary>When the parcel reached a final status: delivered, returned or cancelled (UTC).</summary>
    public DateTime? ClosedOn { get; private set; }

    public byte[] RowVersion { get; private set; } = [];

    public IReadOnlyList<ParcelEvent> Events => events;

    /// <summary>The first <see cref="IncludedWeightGrams"/> cost <see cref="BaseCharge"/>, each started kilogram above it <see cref="ExtraKgCharge"/>.</summary>
    public int IncludedWeightGrams { get; private set; }

    public decimal BaseCharge { get; private set; }

    public decimal ExtraKgCharge { get; private set; }

    /// <summary>
    /// The rates this parcel was booked at, kept on the parcel so a reweigh prices it the way it was sold and never
    /// reaches a rate card that has changed since.
    /// </summary>
    public ParcelCharges Charges => new(
        ServiceArea, DeliveryCharge, CodChargePercent, ReturnCharge, DeliveryDays, IncludedWeightGrams, BaseCharge, ExtraKgCharge)
    {
        Speed = Speed,
        ProductType = ProductType,
        SpeedCharge = SpeedCharge,
        ProductCharge = ProductCharge
    };

    public bool IsFinal => Status.IsFinal();

    /// <summary>The courier has the parcel and it is still on its way: the merchant can ask to cancel it or change its cash.</summary>
    public bool CanBeAskedAbout => ParcelStatuses.ToDeliver.Contains(Status);

    /// <summary>Still to be delivered after the day it was due. A parcel going back to the merchant is not late.</summary>
    public bool IsLate(DateOnly today)
    {
        return DueOn < today && ParcelStatuses.ToDeliver.Contains(Status);
    }

    /// <summary>What the merchant pays for the parcel so far; final once the parcel is.</summary>
    public decimal TotalCharge => Status switch
    {
        ParcelStatus.Delivered or ParcelStatus.PartlyDelivered => DeliveryCharge + (CodCharge ?? 0),
        ParcelStatus.Returned => DeliveryCharge + ReturnCharge,
        ParcelStatus.Cancelled => 0,
        _ => DeliveryCharge + ParcelCharges.CodCharge(CodAmount, CodChargePercent)
    };

    public static Result<Parcel> Create(NewParcel spec)
    {
        var parcel = new Parcel
        {
            MerchantId = spec.MerchantId,
            PickupPointId = spec.PickupPointId,
            PickupHubId = spec.PickupHubId,
            MerchantReference = spec.MerchantReference.NullIfBlank(),
            IdempotencyKey = spec.IdempotencyKey.NullIfBlank(),
            RequestHash = spec.RequestHash,
            Status = ParcelStatus.Pending
        };
        if (parcel.MerchantReference?.Length > 100)
        {
            return Error.Validation("parcel.reference", "The invoice or order number is at most 100 characters.");
        }

        var applied = parcel.Apply(spec.Details);
        if (applied.IsFailure)
        {
            return applied.Error!;
        }

        parcel.events.Add(new ParcelEvent(parcel, ParcelStatus.Pending, "Parcel booked", null));
        parcel.Raise(new ParcelBooked(parcel));

        return parcel;
    }

    /// <summary>The merchant corrects the parcel before it is picked up; the caller prices it again.</summary>
    public Result Edit(ParcelDetails details)
    {
        if (Status != ParcelStatus.Pending)
        {
            return Error.Conflict("parcel.edit", $"{TrackingCode} has been picked up and can no longer be changed.");
        }

        var applied = Apply(details);
        if (applied.IsSuccess)
        {
            events.Add(new ParcelEvent(this, Status, "Parcel details changed by the merchant", null));
        }

        return applied;
    }

    /// <summary>The merchant calls the parcel off before it is picked up.</summary>
    public Result Cancel(string? reason, DateTime now)
    {
        if (Status != ParcelStatus.Pending)
        {
            return Error.Conflict(
                "parcel.cancel",
                $"{TrackingCode} has been picked up. Ask for it to be returned instead.");
        }

        ClosedOn = now;
        MoveTo(ParcelStatus.Cancelled, reason.NullIfBlank() is { } why ? $"Cancelled: {why}" : "Cancelled by the merchant");

        return Result.Success();
    }

    /// <summary>
    /// A rider collected the parcel from the merchant on <paramref name="today"/> (the courier's date), which starts the
    /// days it has to be delivered in. Collecting it again changes nothing.
    /// </summary>
    public Result<ScanOutcome> PickUp(DateOnly today)
    {
        if (Status == ParcelStatus.PickedUp)
        {
            return ScanOutcome.AlreadyRecorded;
        }

        if (Status != ParcelStatus.Pending)
        {
            return Error.Conflict("parcel.pickup", $"{TrackingCode} is {Status.DisplayName().ToLowerInvariant()}, so there is nothing to collect.");
        }

        DueOn = DeliveryDays is { } days ? today.AddDays(days) : null;
        MoveTo(ParcelStatus.PickedUp, "Picked up from the merchant");

        return ScanOutcome.Recorded;
    }

    /// <summary>
    /// Hub staff scanned the parcel in at <paramref name="hubId"/> on <paramref name="today"/>: from the pickup rider (or
    /// the merchant's own drop-off, which starts its days to be delivered in), off the transfer from another hub, or back
    /// from a rider who could not deliver it. Scanning it again at the same hub changes nothing.
    /// </summary>
    public Result<ScanOutcome> ReceiveAt(long hubId, DateOnly today)
    {
        if (CurrentHubId == hubId)
        {
            return ScanOutcome.AlreadyRecorded;
        }

        switch (Status)
        {
            case ParcelStatus.Pending or ParcelStatus.PickedUp:
                var dropped = Status == ParcelStatus.Pending;
                if (dropped)
                {
                    DueOn = DeliveryDays is { } days ? today.AddDays(days) : null;
                }

                Place(hubId);
                MoveTo(ParcelStatus.AtHub, dropped ? "Dropped off at the hub by the merchant" : "Received at the hub", hubId);
                return ScanOutcome.Recorded;

            case ParcelStatus.InTransit:
                Place(hubId);
                MoveTo(ParcelStatus.AtHub, "Arrived at the hub", hubId);
                return ScanOutcome.Recorded;

            case ParcelStatus.OnHold or ParcelStatus.Returning when CurrentHubId is null:
                var fromRider = RiderId is not null;
                Place(hubId);
                events.Add(new ParcelEvent(this, Status, fromRider ? "Back at the hub from the rider" : "Arrived at the hub", hubId));
                return ScanOutcome.Recorded;

            case ParcelStatus.AtHub or ParcelStatus.OnHold or ParcelStatus.Returning:
                return Error.Conflict(
                    "parcel.scan.otherHub",
                    $"{TrackingCode} is recorded at another hub. Dispatch it from there before scanning it in here.");

            case ParcelStatus.OutForDelivery:
                return Error.Conflict(
                    "parcel.scan.withRider",
                    $"{TrackingCode} is out with a rider. The rider records what happened at the door first.");

            default:
                return Error.Conflict(
                    "parcel.scan.final",
                    $"{TrackingCode} is {Status.DisplayName().ToLowerInvariant()}; there is nothing more to scan.");
        }
    }

    /// <summary>
    /// Hub staff sent the parcel from <paramref name="fromHubId"/> to <paramref name="toHubId"/>: forward towards the hub
    /// that delivers it, or, when it is returning, back towards the hub that collected it. Sending it again to the same
    /// hub changes nothing.
    /// </summary>
    public Result<ScanOutcome> DispatchTo(long fromHubId, long toHubId)
    {
        if (TransferToHubId == toHubId && Status is ParcelStatus.InTransit or ParcelStatus.Returning)
        {
            return ScanOutcome.AlreadyRecorded;
        }

        if (CurrentHubId != fromHubId || Status is not (ParcelStatus.AtHub or ParcelStatus.Returning))
        {
            return Error.Conflict(
                "parcel.dispatch.notHere",
                $"{TrackingCode} is not at this hub. Scan it in before sending it on.");
        }

        if (toHubId == fromHubId)
        {
            return Error.Validation("parcel.dispatch.sameHub", "Choose another hub to send the parcel to.");
        }

        if (Status == ParcelStatus.AtHub && DeliveryHubId == fromHubId)
        {
            return Error.Conflict(
                "parcel.dispatch.deliverHere",
                $"{TrackingCode} is delivered from this hub. Assign it to a rider instead.");
        }

        if (Status == ParcelStatus.Returning && PickupHubId == fromHubId)
        {
            return Error.Conflict(
                "parcel.dispatch.returnHere",
                $"{TrackingCode} goes back to its merchant from this hub. Hand it back instead.");
        }

        CurrentHubId = null;
        TransferToHubId = toHubId;
        if (Status == ParcelStatus.Returning)
        {
            events.Add(new ParcelEvent(this, Status, "Sent back towards the merchant's hub", fromHubId));
        }
        else
        {
            MoveTo(ParcelStatus.InTransit, "Sent to the delivery hub", fromHubId);
        }

        return ScanOutcome.Recorded;
    }

    /// <summary>
    /// The hub handed the parcel to rider <paramref name="riderId"/> to deliver. Only from the hub that delivers it, and
    /// only while it waits there for a rider (first time or after a hold).
    /// </summary>
    public Result AssignTo(long riderId, long hubId)
    {
        if (Status is not (ParcelStatus.AtHub or ParcelStatus.OnHold) || CurrentHubId != hubId)
        {
            return Error.Conflict(
                "parcel.assign.notHere",
                $"{TrackingCode} is not waiting at this hub for a rider.");
        }

        if (DeliveryHubId != hubId)
        {
            return Error.Conflict(
                "parcel.assign.otherHub",
                $"{TrackingCode} is delivered from another hub. Send it there first.");
        }

        if (Issue is { } issue)
        {
            return Error.Conflict(
                "parcel.assign.flagged",
                $"{TrackingCode} is {issue.DisplayName().ToLowerInvariant()} ({IssueNote}). Clear the flag on its page before it goes out.");
        }

        CurrentHubId = null;
        RiderId = riderId;
        HoldReason = null;
        HoldUntil = null;
        MoveTo(ParcelStatus.OutForDelivery, "Out for delivery with the rider", hubId);

        return Result.Success();
    }

    /// <summary>
    /// The rider handed the parcel over and collected <paramref name="collected"/>: the whole cash on delivery
    /// (delivered), or less when the recipient kept only part of it (partly delivered, with the reason).
    /// </summary>
    public Result Deliver(decimal collected, string? reason, DateTime now)
    {
        if (Status != ParcelStatus.OutForDelivery)
        {
            return NotOutForDelivery();
        }

        if (collected < 0 || collected > CodAmount)
        {
            return Error.Validation(
                "parcel.deliver.amount",
                $"The amount collected must be between ৳0 and the cash on delivery, ৳{CodAmount:N0}.");
        }

        var partly = collected < CodAmount;
        var why = reason.NullIfBlank();
        if (partly && why is null)
        {
            return Error.Validation(
                "parcel.deliver.reason",
                "Say why less than the cash on delivery was collected, such as \"kept 2 of 3 items\".");
        }

        Attempts++;
        CollectedAmount = collected;
        CodCharge = ParcelCharges.CodCharge(collected, CodChargePercent);
        RiderId = null;
        ClosedOn = now;
        MoveTo(
            partly ? ParcelStatus.PartlyDelivered : ParcelStatus.Delivered,
            partly ? $"Partly delivered, ৳{collected:N0} collected: {why}" : $"Delivered, ৳{collected:N0} collected");

        return Result.Success();
    }

    /// <summary>
    /// The recipient could not take the parcel today (not reachable, asked for another day). The rider brings it back to
    /// the hub for another attempt; after <paramref name="maxAttempts"/> attempts it must be delivered or returned. The
    /// day the recipient asked for, when given, is after <paramref name="today"/> (the courier's date), and the parcel is
    /// not late until then.
    /// </summary>
    public Result Hold(string? reason, DateOnly? until, DateOnly today, int maxAttempts)
    {
        if (Status != ParcelStatus.OutForDelivery)
        {
            return NotOutForDelivery();
        }

        var why = reason.NullIfBlank();
        if (why is null || why.Length > 200)
        {
            return Error.Validation("parcel.hold.reason", "Say why the parcel could not be delivered, at most 200 characters.");
        }

        if (until <= today)
        {
            return Error.Validation("parcel.hold.until", "The day the customer asked for must be after today.");
        }

        if (Attempts + 1 >= maxAttempts)
        {
            return Error.Conflict(
                "parcel.hold.attempts",
                $"This is attempt {Attempts + 1} of {maxAttempts}: deliver the parcel or return it to the merchant.");
        }

        Attempts++;
        HoldReason = why;
        HoldUntil = until;
        if (until > DueOn)
        {
            DueOn = until;
        }

        MoveTo(ParcelStatus.OnHold, until is null ? $"On hold: {why}" : $"On hold until {until:ddd d MMM}: {why}");

        return Result.Success();
    }

    /// <summary>The recipient refused the parcel at the door: it goes back to the merchant.</summary>
    public Result Refuse(string? reason)
    {
        if (Status != ParcelStatus.OutForDelivery)
        {
            return NotOutForDelivery();
        }

        var why = reason.NullIfBlank();
        if (why is null || why.Length > 200)
        {
            return Error.Validation("parcel.refuse.reason", "Say why the recipient did not take the parcel, at most 200 characters.");
        }

        Attempts++;
        ReturnReason = why;
        MoveTo(ParcelStatus.Returning, $"Returning to the merchant: {why}");

        return Result.Success();
    }

    /// <summary>
    /// The merchant or the hub asks for a parcel not yet out with a rider to come back. It returns from wherever it is.
    /// </summary>
    public Result RequestReturn(string? reason)
    {
        if (Status is not (ParcelStatus.PickedUp or ParcelStatus.AtHub or ParcelStatus.InTransit or ParcelStatus.OnHold))
        {
            return Error.Conflict(
                "parcel.return",
                Status == ParcelStatus.Pending
                    ? $"{TrackingCode} has not been picked up yet. Cancel it instead."
                    : $"{TrackingCode} is {Status.DisplayName().ToLowerInvariant()} and cannot be returned now.");
        }

        var why = reason.NullIfBlank() ?? "Return requested";
        if (why.Length > 200)
        {
            return Error.Validation("parcel.return.reason", "Say why in at most 200 characters.");
        }

        ReturnReason = why;
        MoveTo(ParcelStatus.Returning, $"Returning to the merchant: {why}", CurrentHubId);

        return Result.Success();
    }

    /// <summary>
    /// A hub put the parcel on its scale. Merchants routinely under-declare, so the charge follows the measurement,
    /// priced at the rates the parcel was booked under. The merchant's own figure is kept, and the change is written
    /// to the history where the merchant can see both.
    /// </summary>
    public Result Reweigh(int measuredGrams, long? hubId)
    {
        if (!ParcelStatuses.ToDeliver.Contains(Status))
        {
            return Error.Conflict(
                "parcel.reweigh",
                Status == ParcelStatus.Pending
                    ? $"{TrackingCode} has not been picked up yet, so there is nothing to weigh."
                    : $"{TrackingCode} is {Status.DisplayName().ToLowerInvariant()} and its charge is settled.");
        }

        if (measuredGrams is < 1 or > MaxWeightGrams)
        {
            return Error.Validation("parcel.reweigh.weight", $"The weight must be between 1 g and {MaxWeightGrams / 1000} kg.");
        }

        var was = BilledWeightGrams;
        var charged = DeliveryCharge;
        MeasuredWeightGrams = measuredGrams;
        DeliveryCharge = Charges.ForWeight(measuredGrams).DeliveryCharge;

        var note = measuredGrams == was && DeliveryCharge == charged
            ? $"Weighed at the hub: {Weight.Kg(measuredGrams)}, as booked"
            : $"Weighed at the hub: {Weight.Kg(was)} booked, {Weight.Kg(measuredGrams)} measured; " +
              $"delivery charge {charged:N0} to {DeliveryCharge:N0}";
        events.Add(new ParcelEvent(this, Status, note, hubId ?? CurrentHubId));

        return Result.Success();
    }

    /// <summary>
    /// A hub flags a problem for the courier to look at: <see cref="ParcelIssue.InReview"/> while something is checked,
    /// <see cref="ParcelIssue.Exceptional"/> when something has gone wrong. Only while the courier has the parcel.
    /// Flagging it again changes the kind or the words and keeps the time it was first flagged. Until the flag is
    /// cleared the parcel is not handed to a rider.
    /// </summary>
    public Result Flag(ParcelIssue issue, string? note, long? hubId, DateTime now)
    {
        if (!ParcelStatuses.InProgress.Contains(Status))
        {
            return Error.Conflict(
                "parcel.flag",
                Status == ParcelStatus.Pending
                    ? $"{TrackingCode} has not been picked up yet, so there is nothing for the courier to look at."
                    : $"{TrackingCode} is {Status.DisplayName().ToLowerInvariant()}; there is nothing left to flag.");
        }

        if (!Enum.IsDefined(issue))
        {
            return Error.Validation("parcel.flag.kind", "Choose whether the parcel is in review or exceptional.");
        }

        var why = note.NullIfBlank();
        if (why is null || why.Length > 200)
        {
            return Error.Validation("parcel.flag.note", "Say what the problem is, at most 200 characters.");
        }

        IssueRaisedOn ??= now;
        Issue = issue;
        IssueNote = why;
        events.Add(new ParcelEvent(this, Status, $"{issue.DisplayName()}: {why}", hubId ?? CurrentHubId));

        return Result.Success();
    }

    /// <summary>The courier has looked at the problem and the parcel can go on; <paramref name="note"/> says what was done.</summary>
    public Result ClearFlag(string? note, long? hubId)
    {
        if (Issue is not { } issue)
        {
            return Error.Conflict("parcel.flag.none", $"{TrackingCode} has no problem flagged.");
        }

        var done = note.NullIfBlank();
        if (done?.Length > 200)
        {
            return Error.Validation("parcel.flag.note", "Say what was done in at most 200 characters.");
        }

        Issue = null;
        IssueNote = null;
        IssueRaisedOn = null;
        var settled = issue == ParcelIssue.InReview ? "Review finished" : "Exception settled";
        events.Add(new ParcelEvent(this, Status, done is null ? settled : $"{settled}: {done}", hubId ?? CurrentHubId));

        return Result.Success();
    }

    /// <summary>
    /// The cash on delivery becomes <paramref name="amount"/>, at the merchant's request and with the courier's
    /// approval: the rider collects the new amount and the COD charge follows it. Only while the parcel is on its way.
    /// </summary>
    public Result ChangeCod(decimal amount, string reason)
    {
        if (!CanBeAskedAbout)
        {
            return Error.Conflict(
                "parcel.cod.change",
                $"{TrackingCode} is {Status.DisplayName().ToLowerInvariant()}; its cash on delivery can no longer change.");
        }

        if (amount is < 0 or > MaxCodAmount)
        {
            return Error.Validation("parcel.cod", $"The cash on delivery must be between ৳0 and ৳{MaxCodAmount:N0}.");
        }

        var was = CodAmount;
        CodAmount = amount;
        events.Add(new ParcelEvent(this, Status, $"Cash on delivery changed from ৳{was:N0} to ৳{amount:N0}: {reason}", CurrentHubId));

        return Result.Success();
    }

    /// <summary>
    /// Whether the parcel can go back to its merchant with a rider from <paramref name="hubId"/>: returning, waiting at
    /// that hub, which is the one that collected it, and with no problem flagged.
    /// </summary>
    public Result CanGoBackFrom(long hubId)
    {
        if (Status != ParcelStatus.Returning || CurrentHubId != hubId)
        {
            return Error.Conflict(
                "parcel.sendBack.notHere",
                $"{TrackingCode} is not waiting at this hub to go back to its merchant.");
        }

        if (PickupHubId != hubId)
        {
            return Error.Conflict(
                "parcel.sendBack.otherHub",
                $"{TrackingCode} goes back to its merchant from another hub. Send it there first.");
        }

        if (Issue is { } issue)
        {
            return Error.Conflict(
                "parcel.sendBack.flagged",
                $"{TrackingCode} is {issue.DisplayName().ToLowerInvariant()} ({IssueNote}). Clear the flag on its page before it goes out.");
        }

        return Result.Success();
    }

    /// <summary>
    /// The hub that collected a returning parcel sends it back to the merchant with rider <paramref name="riderId"/>, on
    /// a return list, instead of handing it over at its counter. It stays returning until the rider hands it over.
    /// </summary>
    public Result SendBack(long riderId, long hubId)
    {
        var allowed = CanGoBackFrom(hubId);
        if (allowed.IsFailure)
        {
            return allowed;
        }

        CurrentHubId = null;
        RiderId = riderId;
        events.Add(new ParcelEvent(this, Status, "Out to the merchant with the rider", hubId));

        return Result.Success();
    }

    /// <summary>The rider of a return list handed the parcel back to its merchant at the merchant's door. Final.</summary>
    public Result HandBackAtDoor(long riderId, DateTime now)
    {
        if (Status != ParcelStatus.Returning || RiderId != riderId)
        {
            return NotWithRiderToHandBack();
        }

        RiderId = null;
        ClosedOn = now;
        MoveTo(ParcelStatus.Returned, "Handed back to the merchant by the rider");

        return Result.Success();
    }

    /// <summary>The rider could not hand the parcel back; it stays with them until the hub scans it in again.</summary>
    public Result MissHandBack(long riderId, string reason)
    {
        if (Status != ParcelStatus.Returning || RiderId != riderId)
        {
            return NotWithRiderToHandBack();
        }

        events.Add(new ParcelEvent(this, Status, $"Not handed back to the merchant: {reason}", null));

        return Result.Success();
    }

    /// <summary>The hub that collected a returning parcel handed it back to the merchant. Final.</summary>
    public Result<ScanOutcome> ReturnToMerchant(long hubId, DateTime now)
    {
        if (Status == ParcelStatus.Returned)
        {
            return ScanOutcome.AlreadyRecorded;
        }

        if (Status != ParcelStatus.Returning)
        {
            return Error.Conflict(
                "parcel.handBack",
                $"{TrackingCode} is {Status.DisplayName().ToLowerInvariant()}; only a returning parcel goes back to the merchant.");
        }

        if (CurrentHubId != hubId)
        {
            return Error.Conflict("parcel.handBack.notHere", $"{TrackingCode} is not at this hub. Scan it in first.");
        }

        if (PickupHubId != hubId)
        {
            return Error.Conflict(
                "parcel.handBack.otherHub",
                $"{TrackingCode} goes back to its merchant from another hub. Send it there first.");
        }

        CurrentHubId = null;
        ClosedOn = now;
        MoveTo(ParcelStatus.Returned, "Handed back to the merchant", hubId);

        return ScanOutcome.Recorded;
    }

    private Result Apply(ParcelDetails details)
    {
        var name = details.RecipientName.NullIfBlank();
        if (name is null || name.Length > 200)
        {
            return Error.Validation("parcel.recipient.name", "Enter the recipient's name, at most 200 characters.");
        }

        var address = details.RecipientAddress.NullIfBlank();
        if (address is null || address.Length > 500)
        {
            return Error.Validation("parcel.recipient.address", "Enter the delivery address, at most 500 characters.");
        }

        if (details.WeightGrams is < 1 or > MaxWeightGrams)
        {
            return Error.Validation("parcel.weight", $"The weight must be between 1 g and {MaxWeightGrams / 1000} kg.");
        }

        if (details.CodAmount is < 0 or > MaxCodAmount)
        {
            return Error.Validation("parcel.cod", $"The cash on delivery must be between ৳0 and ৳{MaxCodAmount:N0}.");
        }

        if (details.ItemDescription?.Trim().Length > 200 || details.Note?.Trim().Length > 500)
        {
            return Error.Validation("parcel.text", "The item description is at most 200 characters and the note 500.");
        }

        AreaId = details.AreaId;
        DeliveryHubId = details.DeliveryHubId;
        RecipientName = name;
        RecipientPhone = details.RecipientPhone.Value;
        RecipientAddress = address;
        RecipientLatitude = details.Pin?.Latitude;
        RecipientLongitude = details.Pin?.Longitude;
        CodAmount = details.CodAmount;
        WeightGrams = details.WeightGrams;
        ItemDescription = details.ItemDescription.NullIfBlank();
        Note = details.Note.NullIfBlank();
        ServiceArea = details.Charges.ServiceArea;
        DeliveryCharge = details.Charges.DeliveryCharge;
        CodChargePercent = details.Charges.CodChargePercent;
        ReturnCharge = details.Charges.ReturnCharge;
        DeliveryDays = details.Charges.DeliveryDays;
        IncludedWeightGrams = details.Charges.IncludedWeightGrams;
        BaseCharge = details.Charges.BaseCharge;
        ExtraKgCharge = details.Charges.ExtraKgCharge;
        Speed = details.Charges.Speed;
        ProductType = details.Charges.ProductType;
        SpeedCharge = details.Charges.SpeedCharge;
        ProductCharge = details.Charges.ProductCharge;

        return Result.Success();
    }

    private void Place(long hubId)
    {
        CurrentHubId = hubId;
        TransferToHubId = null;
        RiderId = null;
    }

    private void MoveTo(ParcelStatus status, string note, long? hubId = null)
    {
        Status = status;
        events.Add(new ParcelEvent(this, status, note, hubId));
        Raise(new ParcelStatusChanged(this, status));
    }

    private Error NotWithRiderToHandBack()
    {
        return Error.Conflict("parcel.handBack.notWithRider", $"{TrackingCode} is not with this rider to hand back.");
    }

    private Error NotOutForDelivery()
    {
        return Error.Conflict(
            "parcel.notOut",
            $"{TrackingCode} is {Status.DisplayName().ToLowerInvariant()}, not out for delivery.");
    }
}
