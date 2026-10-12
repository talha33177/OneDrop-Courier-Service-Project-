using Microsoft.EntityFrameworkCore;
using Application.Abstractions;
using Domain.Common;
using Domain.Merchants;
using Domain.Network;
using Domain.Pricing;

namespace Application.Parcels;

/// <summary>
/// Where a parcel goes and what it costs: the pickup point and its hub, the recipient's area and its hub, the charges,
/// and the rate they come from (which also says what other speeds and contents the route takes).
/// </summary>
public sealed record Booking(PickupPoint PickupPoint, long PickupHubId, Area Area, Hub DeliveryHub, ParcelCharges Charges, DeliveryRate Rate);

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
        DeliverySpeed speed,
        ProductType product,
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
        var charges = rate.Price(weightGrams, speed, product);
        if (charges.IsFailure)
        {
            return charges.Error!;
        }

        return new Booking(point, pickupZone.HubId, area, area.Zone!.Hub!, charges.Value, rate);
    }
}
