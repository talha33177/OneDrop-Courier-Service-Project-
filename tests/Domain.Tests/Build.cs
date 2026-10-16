using System.Reflection;
using Domain.Common;
using Domain.Delivery;
using Domain.Merchants;
using Domain.Network;
using Domain.Parcels;
using Domain.Pricing;

namespace Domain.Tests;

/// <summary>Ready-made domain objects for the tests, with the Dhaka launch rates written out here, not read from code.</summary>
internal static class Build
{
    public const long Mirpur = 1;
    public const long Gulshan = 2;
    public const long Sylhet = 3;

    /// <summary>The courier's date the tests' parcels are picked up on.</summary>
    public static readonly DateOnly Today = new(2026, 10, 4);

    public static readonly RateValues InsideCity = new(1000, 60, 15, 1, 0, 1);
    public static readonly RateValues Suburb = new(1000, 100, 20, 1, 50, 2);
    public static readonly RateValues OutsideCity = new(1000, 120, 20, 1, 60, 3);

    /// <summary>The default speed: everywhere, at no extra, in the rate card's time.</summary>
    public static DeliveryOption Regular() => Option(11, DeliveryOptionKind.Speed, "regular", "Regular", isDefault: true);

    /// <summary>The launch express: ৳40, the day after pickup, inside the city and to a suburb only; goes first.</summary>
    public static DeliveryOption Express() =>
        Option(12, DeliveryOptionKind.Speed, "express", "Express", goesFirst: true, near: 40, days: 1);

    /// <summary>The default contents: everywhere, at no extra.</summary>
    public static DeliveryOption General() => Option(21, DeliveryOptionKind.Contents, "general", "General", isDefault: true);

    public static DeliveryOption Liquid() => Option(22, DeliveryOptionKind.Contents, "liquid", "Liquid", near: 10, far: 10);

    public static DeliveryOption Fragile() => Option(23, DeliveryOptionKind.Contents, "fragile", "Fragile", near: 20, far: 20);

    /// <summary>The launch frozen: ৳50, inside the city and to a suburb only; goes first.</summary>
    public static DeliveryOption Frozen() =>
        Option(25, DeliveryOptionKind.Contents, "frozen", "Frozen", goesFirst: true, near: 50);

    /// <summary>
    /// A delivery option priced <paramref name="near"/> inside the city and to a suburb and <paramref name="far"/>
    /// outside it (null: not offered there), a speed delivering in <paramref name="days"/> where it is offered.
    /// </summary>
    public static DeliveryOption Option(
        long id,
        DeliveryOptionKind kind,
        string code,
        string name,
        bool isDefault = false,
        bool goesFirst = false,
        decimal? near = null,
        decimal? far = null,
        int? days = null)
    {
        var option = DeliveryOption.Create(
            kind,
            code,
            new DeliveryOptionValues(name, null, kind == DeliveryOptionKind.Contents ? $"{name}: handle with care" : null, "package", goesFirst),
            (int)id).Value;
        if (isDefault)
        {
            option.MakeDefault();
        }

        option.SetCharge(ServiceArea.InsideCity, near, near is null ? null : days);
        option.SetCharge(ServiceArea.Suburb, near, near is null ? null : days);
        option.SetCharge(ServiceArea.OutsideCity, far, far is null ? null : days);

        return WithId(option, id);
    }

    /// <summary>
    /// The launch courier's vehicles: a bicycle carries 25 parcels, 15 kg and 5 kg a parcel; a motorbike 40, 30 kg and
    /// 10 kg; a pickup van 300, a tonne and 30 kg.
    /// </summary>
    public static Fleet Fleet()
    {
        return new Fleet(
        [
            VehicleCapacity.Create(Vehicle.Bicycle, new CapacityValues(25, 15_000, 5_000)).Value,
            VehicleCapacity.Create(Vehicle.Motorbike, new CapacityValues(40, 30_000, 10_000)).Value,
            VehicleCapacity.Create(Vehicle.Van, new CapacityValues(300, 1_000_000, 30_000)).Value
        ]);
    }

    public static Merchant Merchant(long id = 7)
    {
        var merchant = Domain.Merchants.Merchant.Add(
            new MerchantProfile("Fashion House", "Nusrat Jahan", "01711000001", null, "House 7, Mirpur 10")).Value;

        return WithId(merchant, id);
    }

    public static DeliveryRate Rate(ServiceArea area, RateValues? values = null)
    {
        return DeliveryRate.Create(area, values ?? area switch
        {
            ServiceArea.InsideCity => InsideCity,
            ServiceArea.Suburb => Suburb,
            _ => OutsideCity
        }).Value;
    }

    /// <summary>A booked parcel of merchant 7 with an id, as if saved.</summary>
    public static Parcel Parcel(
        long pickupHub = Mirpur,
        long deliveryHub = Mirpur,
        decimal cod = 1250,
        int grams = 500,
        ServiceArea area = ServiceArea.InsideCity,
        long id = 100,
        long pickupPoint = 1)
    {
        var parcel = Domain.Parcels.Parcel.Create(new NewParcel(
            7,
            pickupPoint,
            pickupHub,
            new ParcelDetails(
                1,
                deliveryHub,
                "Rahim Uddin",
                PhoneNumber.Parse("01811000101").Value,
                "House 22, Road 4",
                cod,
                grams,
                "Panjabi",
                null,
                Rate(area).ChargesFor(grams)))).Value;
        typeof(Parcel).GetProperty(nameof(Domain.Parcels.Parcel.TrackingCode))!.SetValue(parcel, $"OD{10000000 + id}");

        return WithId(parcel, id);
    }

    /// <summary>A parcel out with rider 5, delivered from Mirpur.</summary>
    public static Parcel OutForDelivery(decimal cod = 1250)
    {
        var parcel = Parcel(cod: cod);
        parcel.PickUp(Build.Today);
        parcel.ReceiveAt(Mirpur, Build.Today);
        parcel.AssignTo(5, Mirpur);
        parcel.ClearDomainEvents();

        return parcel;
    }

    /// <summary>A parcel refused at the door and back at Mirpur, the hub that collected it, waiting to go back to merchant 7.</summary>
    public static Parcel Returning(long id = 100, long pickupPoint = 1, long pickupHub = Mirpur)
    {
        var parcel = Parcel(pickupHub: pickupHub, id: id, pickupPoint: pickupPoint);
        parcel.PickUp(Build.Today);
        parcel.ReceiveAt(Mirpur, Build.Today);
        parcel.AssignTo(5, Mirpur);
        parcel.Refuse("Customer was not at home");
        parcel.ReceiveAt(Mirpur, Build.Today);
        parcel.ClearDomainEvents();

        return parcel;
    }

    /// <summary>An active rider of <paramref name="hub"/> with an id, as if saved.</summary>
    public static Rider Rider(long hub = Mirpur, long id = 5)
    {
        return WithId(Domain.Delivery.Rider.Create(hub, "Rafiq Hasan", "01722000001", Vehicle.Motorbike, null).Value, id);
    }

    public static T WithId<T>(T entity, long id)
        where T : Entity
    {
        typeof(Entity).GetProperty(nameof(Entity.Id), BindingFlags.Public | BindingFlags.Instance)!.SetValue(entity, id);

        return entity;
    }
}
