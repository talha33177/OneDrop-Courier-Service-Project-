using Domain.Common;

namespace Domain.Pricing;

/// <summary>
/// What a parcel costs the merchant and how soon it is delivered, worked out when it is booked and kept on the parcel.
/// The delivery charge is the weight's charge plus the extras for the parcel's speed and for what is inside.
/// </summary>
/// <param name="CodChargePercent">Taken off the cash collected when the parcel is delivered.</param>
/// <param name="ReturnCharge">Added to the delivery charge when the parcel comes back to the merchant.</param>
/// <param name="DeliveryDays">Days after pickup the courier promises to deliver in; null when it promises none.</param>
public sealed record ParcelCharges(
    ServiceArea ServiceArea,
    decimal DeliveryCharge,
    decimal CodChargePercent,
    decimal ReturnCharge,
    int? DeliveryDays,
    int IncludedWeightGrams,
    decimal BaseCharge,
    decimal ExtraKgCharge)
{
    /// <summary>The speed (<see cref="DeliveryOption"/>) it goes at.</summary>
    public long SpeedId { get; init; }

    /// <summary>The <see cref="DeliveryOption"/> saying what is inside.</summary>
    public long ProductTypeId { get; init; }

    /// <summary>What the speed adds, or 0 for the default speed.</summary>
    public decimal SpeedCharge { get; init; }

    /// <summary>What the contents add (fragile, frozen and so on), or 0 for the default contents.</summary>
    public decimal ProductCharge { get; init; }

    /// <summary>The delivery charge these rates give a parcel weighing <paramref name="weightGrams"/>, extras included.</summary>
    public decimal DeliveryChargeFor(int weightGrams)
    {
        var extraGrams = Math.Max(0, weightGrams - IncludedWeightGrams);
        var extraKg = (extraGrams + 999) / 1000;

        return BaseCharge + extraKg * ExtraKgCharge + SpeedCharge + ProductCharge;
    }

    /// <summary>The same rates applied to a corrected weight, so a reweigh never reaches today's rate card.</summary>
    public ParcelCharges ForWeight(int weightGrams)
    {
        return this with { DeliveryCharge = DeliveryChargeFor(weightGrams) };
    }

    /// <summary>The COD charge on <paramref name="collected"/>, rounded to the whole taka.</summary>
    public decimal CodChargeOn(decimal collected)
    {
        return CodCharge(collected, CodChargePercent);
    }

    public static decimal CodCharge(decimal collected, decimal percent)
    {
        return Math.Round(collected * percent / 100m, 0, MidpointRounding.AwayFromZero);
    }
}

/// <summary>
/// One line of a tenant's rate card: what a parcel in one <see cref="ServiceArea"/> costs the merchant. The first
/// <see cref="IncludedWeightGrams"/> cost <see cref="BaseCharge"/>, each started kilogram above it
/// <see cref="ExtraKgCharge"/>; <see cref="DeliveryDays"/> is how soon after pickup it is delivered. On top come the
/// extras of the parcel's speed and contents (<see cref="DeliveryOption"/>), each priced per service area. Every
/// number is the tenant's own, set by its admin; none has a default.
/// </summary>
public class DeliveryRate : TenantEntity
{
    public const decimal MaxCharge = 10_000m;
    public const decimal MaxCodChargePercent = 10m;
    public const int MaxDeliveryDays = 30;

    private DeliveryRate()
    {
    }

    public ServiceArea ServiceArea { get; private set; }

    public int IncludedWeightGrams { get; private set; }

    public decimal BaseCharge { get; private set; }

    public decimal ExtraKgCharge { get; private set; }

    /// <summary>Per cent of the cash collected at the door.</summary>
    public decimal CodChargePercent { get; private set; }

    /// <summary>Charged on top of the delivery charge for a parcel that comes back to the merchant.</summary>
    public decimal ReturnCharge { get; private set; }

    /// <summary>
    /// Days after pickup the courier promises to deliver in: 0 the same day, 1 the next day. Null when it promises no
    /// time, so parcels get no date to be delivered by.
    /// </summary>
    public int? DeliveryDays { get; private set; }

    public static Result<DeliveryRate> Create(ServiceArea area, RateValues values)
    {
        var rate = new DeliveryRate { ServiceArea = area };
        var changed = rate.Change(values);

        return changed.IsSuccess ? rate : changed.Error!;
    }

    /// <summary>New prices for parcels booked from now on. Parcels already booked keep the charges they were booked at.</summary>
    public Result Change(RateValues values)
    {
        if (values.IncludedWeightGrams is < 1 or > 50_000)
        {
            return Error.Validation("rate.weight", "The included weight must be between 1 g and 50 kg.");
        }

        if (values.BaseCharge is < 0 or > MaxCharge || values.ExtraKgCharge is < 0 or > MaxCharge ||
            values.ReturnCharge is < 0 or > MaxCharge)
        {
            return Error.Validation("rate.amount", $"Charges must be between ৳0 and ৳{MaxCharge:N0}.");
        }

        if (values.CodChargePercent is < 0 or > MaxCodChargePercent)
        {
            return Error.Validation("rate.cod", $"The COD charge must be between 0% and {MaxCodChargePercent}%.");
        }

        if (values.DeliveryDays is < 0 or > MaxDeliveryDays)
        {
            return Error.Validation(
                "rate.days",
                $"The delivery time must be between 0 days (the same day) and {MaxDeliveryDays}, or left empty for no promise.");
        }

        IncludedWeightGrams = values.IncludedWeightGrams;
        BaseCharge = values.BaseCharge;
        ExtraKgCharge = values.ExtraKgCharge;
        CodChargePercent = values.CodChargePercent;
        ReturnCharge = values.ReturnCharge;
        DeliveryDays = values.DeliveryDays;

        return Result.Success();
    }

    /// <summary>The delivery charge for a parcel with no extras weighing <paramref name="weightGrams"/>.</summary>
    public decimal DeliveryChargeFor(int weightGrams)
    {
        return ChargesFor(weightGrams).DeliveryCharge;
    }

    /// <summary>The charges a parcel with no extras weighing <paramref name="weightGrams"/> is booked at.</summary>
    public ParcelCharges ChargesFor(int weightGrams)
    {
        var charges = new ParcelCharges(
            ServiceArea, 0, CodChargePercent, ReturnCharge, DeliveryDays, IncludedWeightGrams, BaseCharge, ExtraKgCharge);

        return charges.ForWeight(weightGrams);
    }

    /// <summary>
    /// The charges a parcel weighing <paramref name="weightGrams"/> is booked at, going at <paramref name="speed"/>
    /// with <paramref name="contents"/> inside, or why the courier does not carry it so in this service area.
    /// </summary>
    public Result<ParcelCharges> Price(int weightGrams, DeliveryOption speed, DeliveryOption contents)
    {
        if (speed.Kind != DeliveryOptionKind.Speed || contents.Kind != DeliveryOptionKind.Contents)
        {
            throw new ArgumentException("A parcel is priced with a speed and a kind of contents.");
        }

        if (speed.Archived || speed.ChargeIn(ServiceArea) is not { } speedCharge)
        {
            return Error.Validation(
                "parcel.speed",
                $"{speed.Name} delivery is not offered {ServiceArea.Destination()}. Choose another speed.");
        }

        if (contents.Archived || contents.ChargeIn(ServiceArea) is not { } productCharge)
        {
            return Error.Validation(
                "parcel.productType",
                $"The courier does not carry {contents.Name.ToLowerInvariant()} parcels {ServiceArea.Destination()}.");
        }

        var charges = new ParcelCharges(
            ServiceArea,
            0,
            CodChargePercent,
            ReturnCharge,
            speed.DaysIn(ServiceArea) ?? DeliveryDays,
            IncludedWeightGrams,
            BaseCharge,
            ExtraKgCharge)
        {
            SpeedId = speed.Id,
            ProductTypeId = contents.Id,
            SpeedCharge = speedCharge,
            ProductCharge = productCharge
        };

        return charges.ForWeight(weightGrams);
    }
}

public sealed record RateValues(
    int IncludedWeightGrams,
    decimal BaseCharge,
    decimal ExtraKgCharge,
    decimal CodChargePercent,
    decimal ReturnCharge,
    int? DeliveryDays);
