using Domain.Network;

namespace Domain.Pricing;

/// <summary>
/// How far a parcel travels, which decides its rate. Stored as TINYINT. Never renumber a value that has been saved.
/// </summary>
public enum ServiceArea : byte
{
    /// <summary>Picked up and delivered in the same city (Mirpur to Gulshan).</summary>
    InsideCity = 1,

    /// <summary>From a city to one of its suburbs (Dhaka to Savar).</summary>
    Suburb = 2,

    /// <summary>To another city or district (Dhaka to Sylhet).</summary>
    OutsideCity = 3
}

public static class ServiceAreas
{
    /// <summary>
    /// The service area of a parcel picked up in <paramref name="pickup"/> and delivered in <paramref name="destination"/>:
    /// another city is outside the city, a suburb of the pickup's city is a suburb, the rest is inside the city.
    /// </summary>
    public static ServiceArea Between(Zone pickup, Zone destination)
    {
        if (!string.Equals(pickup.City, destination.City, StringComparison.OrdinalIgnoreCase))
        {
            return ServiceArea.OutsideCity;
        }

        return destination.IsSuburb ? ServiceArea.Suburb : ServiceArea.InsideCity;
    }

    /// <summary>How the service area is written for people: "Inside city", "Suburb", "Outside city".</summary>
    public static string DisplayName(this ServiceArea area)
    {
        return area switch
        {
            ServiceArea.InsideCity => "Inside city",
            ServiceArea.Suburb => "Suburb",
            _ => "Outside city"
        };
    }

    /// <summary>Where such a parcel goes, to finish a sentence: "inside the city", "to a suburb", "outside the city".</summary>
    public static string Destination(this ServiceArea area)
    {
        return area switch
        {
            ServiceArea.InsideCity => "inside the city",
            ServiceArea.Suburb => "to a suburb",
            _ => "outside the city"
        };
    }
}
