using Domain.Common;

namespace Domain.Merchants;

/// <summary>
/// Where a rider collects a merchant's parcels: a shop, a warehouse or a home. A merchant has one default point, used
/// when a parcel names none. The point's area decides the hub that collects from it and, with the destination, the
/// service area a parcel is charged at. Its door can be pinned on a map (<see cref="Pin"/>), like a recipient's, so
/// the pickup van is guided to it and the stop sits on the rider's route.
/// </summary>
public class PickupPoint : TenantEntity, IMerchantOwned, IArchivable
{
    private PickupPoint()
    {
    }

    public long MerchantId { get; private set; }

    public long AreaId { get; private set; }

    public string Name { get; private set; } = "";

    public string Address { get; private set; } = "";

    /// <summary>E.164: who the rider calls on arrival.</summary>
    public string ContactPhone { get; private set; } = "";

    public bool IsDefault { get; private set; }

    public decimal? Latitude { get; private set; }

    public decimal? Longitude { get; private set; }

    /// <summary>The door on the map, when the merchant pinned it.</summary>
    public GeoPoint? Pin => GeoPoint.From(Latitude, Longitude) is { IsSuccess: true } point ? point.Value : null;

    public bool Archived { get; private set; }

    public static Result<PickupPoint> Create(
        long merchantId,
        long areaId,
        string? name,
        string? address,
        string? phone,
        bool isDefault,
        GeoPoint? pin = null)
    {
        var point = new PickupPoint { MerchantId = merchantId, IsDefault = isDefault };
        var changed = point.Change(areaId, name, address, phone, pin);

        return changed.IsSuccess ? point : changed.Error!;
    }

    public Result Change(long areaId, string? name, string? address, string? phone, GeoPoint? pin = null)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > 200)
        {
            return Error.Validation("pickupPoint.name", "Name the pickup point, such as \"Shop\" or \"Warehouse\".");
        }

        if (string.IsNullOrWhiteSpace(address) || address.Trim().Length > 500)
        {
            return Error.Validation("pickupPoint.address", "Enter the pickup address, at most 500 characters.");
        }

        var contact = PhoneNumber.Parse(phone);
        if (contact.IsFailure)
        {
            return contact.Error!;
        }

        AreaId = areaId;
        Name = name.Trim();
        Address = address.Trim();
        ContactPhone = contact.Value.Value;
        Latitude = pin?.Latitude;
        Longitude = pin?.Longitude;

        return Result.Success();
    }

    public void MakeDefault(bool isDefault)
    {
        IsDefault = isDefault;
    }
}
