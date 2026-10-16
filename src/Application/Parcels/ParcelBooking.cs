using Microsoft.EntityFrameworkCore;
using Application.Abstractions;
using Domain.Common;
using Domain.Merchants;
using Domain.Network;
using Domain.Pricing;

namespace Application.Parcels;

/// <summary>
/// Where a parcel goes and what it costs: the pickup point and its hub, the recipient's area and its hub, the charges,
/// the rate they come from, the speed and contents chosen, and every option on sale (with the rate, they say what
/// else the route takes).
/// </summary>
public sealed record Booking(
    PickupPoint PickupPoint,
    long PickupHubId,
    Area Area,
    Hub DeliveryHub,
    ParcelCharges Charges,
    DeliveryRate Rate,
    DeliveryOption Speed,
    DeliveryOption ProductType,
    IReadOnlyList<DeliveryOption> Options);

/// <summary>
/// Works out a parcel's route and price for booking, editing and quoting: the merchant's pickup point (or its default),
/// the recipient's area (by id or name), the hubs of their zones, and the rate of the service area between them with
/// the extras for the parcel's speed and what is inside.
/// </summary>
public class ParcelBooking(IAppDbContext db)
{
    public static readonly Error UnknownArea =
        Error.Validation("parcel.area.unknown", "The area is not one we deliver to. See the area list (GET /api/v1/areas).");

    public static readonly Error UnknownPickupPoint =
        Error.Validation("parcel.pickupPoint.unknown", "No pickup point found. Add a default pickup point first.");

    public async Task<Result<Booking>> ResolveAsync(
        long merchantId,
        long? pickupPointId,
        long? areaId,
        string? areaName,
        int weightGrams,
        string? speed,
        string? product,
        CancellationToken cancellationToken = default)
    {
        var points = db.PickupPoints.Where(p => p.MerchantId == merchantId && !p.Archived);
        var point = await (pickupPointId.HasValue
                ? points.Where(p => p.Id == pickupPointId.Value)
                : points.Where(p => p.IsDefault))
            .FirstOrDefaultAsync(cancellationToken);
        if (point is null)
        {
            return UnknownPickupPoint;
        }

        var areas = db.Areas
            .Include(a => a.Zone!)
            .ThenInclude(z => z.Hub)
            .Where(a => !a.Archived);
        var name = areaName?.Trim();
        var area = await (areaId.HasValue
                ? areas.Where(a => a.Id == areaId.Value)
                : areas.Where(a => a.Name == name))
            .FirstOrDefaultAsync(cancellationToken);
        if (area is null)
        {
            return UnknownArea;
        }

        var pickupZone = await db.Areas
            .Where(a => a.Id == point.AreaId)
            .Select(a => a.Zone!)
            .SingleAsync(cancellationToken);
        var serviceArea = ServiceAreas.Between(pickupZone, area.Zone!);
        var rate = await db.DeliveryRates.SingleOrDefaultAsync(r => r.ServiceArea == serviceArea, cancellationToken)
            ?? throw new InvalidOperationException($"The courier has no rate for {serviceArea.DisplayName()}.");
        var options = await db.DeliveryOptions
            .Include(o => o.Charges)
            .Where(o => !o.Archived)
            .OrderBy(o => o.SortOrder)
            .ThenBy(o => o.Id)
            .ToListAsync(cancellationToken);
        var chosenSpeed = Choose(options, DeliveryOptionKind.Speed, speed);
        if (chosenSpeed is null)
        {
            return Error.Validation("parcel.speed", Unknown(options, DeliveryOptionKind.Speed, "The speed"));
        }

        var chosenProduct = Choose(options, DeliveryOptionKind.Contents, product);
        if (chosenProduct is null)
        {
            return Error.Validation("parcel.productType", Unknown(options, DeliveryOptionKind.Contents, "What is inside"));
        }

        var charges = rate.Price(weightGrams, chosenSpeed, chosenProduct);
        if (charges.IsFailure)
        {
            return charges.Error!;
        }

        return new Booking(point, pickupZone.HubId, area, area.Zone!.Hub!, charges.Value, rate, chosenSpeed, chosenProduct, options);
    }

    /// <summary>The option <paramref name="text"/> names (by code or name), or the kind's default when it is empty.</summary>
    private static DeliveryOption? Choose(IReadOnlyList<DeliveryOption> options, DeliveryOptionKind kind, string? text)
    {
        var ofKind = options.Where(o => o.Kind == kind);

        return string.IsNullOrWhiteSpace(text)
            ? ofKind.FirstOrDefault(o => o.IsDefault)
            : ofKind.FirstOrDefault(o => o.IsNamedBy(text));
    }

    private static string Unknown(IReadOnlyList<DeliveryOption> options, DeliveryOptionKind kind, string what)
    {
        return $"{what} is one of: {string.Join(", ", options.Where(o => o.Kind == kind).Select(o => o.Code))}, or left out.";
    }
}
