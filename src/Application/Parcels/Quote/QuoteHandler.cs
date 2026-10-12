using Application.Abstractions;
using Domain.Common;
using Domain.Pricing;

namespace Application.Parcels.Quote;

/// <summary>What one kind of contents adds to the delivery charge on a route.</summary>
public sealed record ProductOption(ProductType ProductType, decimal Charge);

/// <summary>
/// What a parcel to <see cref="Area"/> would cost the merchant, before booking it, and how many days after pickup it
/// would be delivered in (null when the courier promises no time). The delivery charge includes
/// <see cref="SpeedCharge"/> and <see cref="ProductCharge"/>. <see cref="ExpressCharge"/> and
/// <see cref="Products"/> say what the route offers: express (null when it is not offered) and the contents it
/// carries, each with its extra.
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
    DeliverySpeed Speed,
    ProductType ProductType,
    decimal SpeedCharge,
    decimal ProductCharge,
    decimal? ExpressCharge,
    int? ExpressDays,
    IReadOnlyList<ProductOption> Products);

/// <summary>The merchant's price calculator: the same route and rate a booking would get now.</summary>
public class QuoteHandler(ICurrentUser currentUser, ParcelBooking booking)
{
    public async Task<Result<QuoteView>> QuoteAsync(
        long? areaId,
        string? areaName,
        long? pickupPointId,
        decimal weightKg,
        decimal codAmount,
        DeliverySpeed speed = DeliverySpeed.Regular,
        ProductType product = ProductType.General,
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

        var charges = route.Value.Charges;
        var rate = route.Value.Rate;
        var cod = charges.CodChargeOn(codAmount);

        return new QuoteView(
            route.Value.Area.Name,
            route.Value.DeliveryHub.Name,
            charges.ServiceArea,
            charges.DeliveryCharge,
            cod,
            charges.DeliveryCharge + cod,
            charges.ReturnCharge,
            charges.DeliveryDays,
            charges.Speed,
            charges.ProductType,
            charges.SpeedCharge,
            charges.ProductCharge,
            rate.ExpressCharge,
            rate.ExpressCharge is null ? null : rate.ExpressDays ?? rate.DeliveryDays,
            [.. DeliveryOptions.ProductTypes.Where(p => rate.ExtraFor(p) is not null).Select(p => new ProductOption(p, rate.ExtraFor(p)!.Value))]);
    }
}
