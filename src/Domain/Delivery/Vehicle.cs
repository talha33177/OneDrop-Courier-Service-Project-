namespace Domain.Delivery;

/// <summary>What a rider rides. Stored as TINYINT. Never renumber a value that has been saved.</summary>
public enum Vehicle : byte
{
    /// <summary>Cheap and quick through Dhaka's lanes, for small parcels close to the hub.</summary>
    Bicycle = 1,

    Motorbike = 2,

    /// <summary>A pickup or covered van with a driver: big merchant pickups and heavy parcels.</summary>
    Van = 3
}

public static class Vehicles
{
    public static readonly Vehicle[] All = [Vehicle.Bicycle, Vehicle.Motorbike, Vehicle.Van];

    /// <summary>
    /// Whether a rider on the vehicle is sent to collect parcels from merchants. Only the pickup van is: bicycles and
    /// motorbikes take parcels to recipients and never go to a merchant.
    /// </summary>
    public static bool CollectsPickups(this Vehicle vehicle)
    {
        return vehicle == Vehicle.Van;
    }

    /// <summary>How the vehicle is written for people: "Bicycle", "Motorbike", "Pickup van".</summary>
    public static string DisplayName(this Vehicle vehicle)
    {
        return vehicle switch
        {
            Vehicle.Bicycle => "Bicycle",
            Vehicle.Motorbike => "Motorbike",
            _ => "Pickup van"
        };
    }
}
