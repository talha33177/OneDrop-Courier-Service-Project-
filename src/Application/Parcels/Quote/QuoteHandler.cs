using Application.Abstractions;
using Domain.Common;
using Domain.Pricing;

namespace Application.Parcels.Quote;

/// <summary>
/// What one speed or kind of contents adds to the delivery charge on a route, and for a speed the days after pickup
/// it is delivered in (null when the courier promises no time).
/// </summary>
public sealed record OptionPrice(string Code, string Name, decimal Charge, int? DeliveryDays);

/// <summary>
/// What a parcel to <see cref="Area"/> would cost the merchant, before booking it, and how many days after pickup it
/// would be delivered in (null when the courier promises no time). The delivery charge includes
/// <see cref="SpeedCharge"/> and <see cref="ProductCharge"/>. <see cref="Speeds"/> and <see cref="Contents"/> say what
/// the route offers, each with its extra; an option the route does not take is left out.
/// </summary>
public sealed record QuoteView(
    string Area,
    string DeliveryHub,
    ServiceArea ServiceArea,
    decimal DeliveryCharge,
    decimal CodCharge,
    decimal Total,
    decimal ReturnCharge,
    int? DeliveryDays,
    string Speed,
    string SpeedName,
    string ProductType,
    string ProductName,
    decimal SpeedCharge,
    decimal ProductCharge,
    IReadOnlyList<OptionPrice> Speeds,
    IReadOnlyList<OptionPrice> Contents);

/// <summary>The merchant's price calculator: the same route and rate a booking would get now.</summary>
public class QuoteHandler(ICurrentUser currentUser, ParcelBooking booking)
{
    public async Task<Result<QuoteView>> QuoteAsync(
        long? areaId,
        string? areaName,
        long? pickupPointId,
        decimal weightKg,
        decimal codAmount,
        string? speed = null,
        string? product = null,
        CancellationToken cancellationToken = default)
    {
        var merchantId = currentUser.MerchantId ?? throw new InvalidOperationException("A merchant asks for a quote.");
        if (weightKg is <= 0 or > Domain.Parcels.Parcel.MaxWeightGrams / 1000m || codAmount < 0)
        {
            return Error.Validation("quote.invalid", "Enter a weight up to 30 kg and a cash amount of ৳0 or more.");
        }

        var route = await booking.ResolveAsync(
            merchantId,
            pickupPointId,
            areaId,
            areaName,
            (int)Math.Round(weightKg * 1000m, MidpointRounding.AwayFromZero),
            speed,
            product,
            cancellationToken);
        if (route.IsFailure)
        {
            return route.Error!;
        }

        var (_, _, area, hub, charges, rate, chosenSpeed, chosenProduct, options) = route.Value;
        var cod = charges.CodChargeOn(codAmount);

        List<OptionPrice> Offered(DeliveryOptionKind kind)
        {
            return
            [
                .. options
                    .Where(o => o.Kind == kind && o.ChargeIn(rate.ServiceArea) is not null)
                    .Select(o => new OptionPrice(
                        o.Code, o.Name, o.ChargeIn(rate.ServiceArea)!.Value, o.DaysIn(rate.ServiceArea) ?? rate.DeliveryDays))
            ];
        }

        return new QuoteView(
            area.Name,
            hub.Name,
            charges.ServiceArea,
            charges.DeliveryCharge,
            cod,
            charges.DeliveryCharge + cod,
            charges.ReturnCharge,
            charges.DeliveryDays,
            chosenSpeed.Code,
            chosenSpeed.Name,
            chosenProduct.Code,
            chosenProduct.Name,
            charges.SpeedCharge,
            charges.ProductCharge,
            Offered(DeliveryOptionKind.Speed),
            Offered(DeliveryOptionKind.Contents));
    }
}
