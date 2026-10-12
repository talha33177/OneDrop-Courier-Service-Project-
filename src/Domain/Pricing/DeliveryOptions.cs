namespace Domain.Pricing;

/// <summary>How fast a parcel goes. Stored as TINYINT. Never renumber a value that has been saved.</summary>
public enum DeliverySpeed : byte
{
    /// <summary>The rate card's delivery time.</summary>
    Regular = 1,

    /// <summary>
    /// Delivered sooner, in the express delivery time of the service area when the courier gives one, and first on the
    /// hub's shelf and the rider's run. Costs the express charge.
    /// </summary>
    Express = 2
}

/// <summary>
/// What is inside a parcel, which decides how it is handled and what extra it costs. Stored as TINYINT. Never
/// renumber a value that has been saved.
/// </summary>
public enum ProductType : byte
{
    /// <summary>Clothes, books, gadgets in their box: no special care.</summary>
    General = 1,

    /// <summary>Oil, drinks, cosmetics in bottles: sealed against leaks and kept upright.</summary>
    Liquid = 2,

    /// <summary>Glass, ceramics, screens: handled with care and never stacked under other parcels.</summary>
    Fragile = 3,

    /// <summary>Cakes, fruit, cooked food: delivered first, before it spoils.</summary>
    FreshFood = 4,

    /// <summary>Ice cream, frozen meat and fish: carried in a cold box and delivered first.</summary>
    Frozen = 5
}

public static class DeliveryOptions
{
    public static readonly ProductType[] ProductTypes = Enum.GetValues<ProductType>();

    public static string DisplayName(this DeliverySpeed speed)
    {
        return speed == DeliverySpeed.Express ? "Express" : "Regular";
    }

    public static string DisplayName(this ProductType product)
    {
        return product switch
        {
            ProductType.Liquid => "Liquid",
            ProductType.Fragile => "Fragile",
            ProductType.FreshFood => "Fresh food",
            ProductType.Frozen => "Frozen",
            _ => "General"
        };
    }

    /// <summary>How the parcel is to be handled, for labels, hubs and riders; null when it needs no special care.</summary>
    public static string? Handling(this ProductType product)
    {
        return product switch
        {
            ProductType.Liquid => "Liquid: keep upright",
            ProductType.Fragile => "Fragile: handle with care",
            ProductType.FreshFood => "Fresh food: deliver first",
            ProductType.Frozen => "Frozen: keep in the cold box",
            _ => null
        };
    }

    /// <summary>Spoils if it waits, so it goes out first like an express parcel.</summary>
    public static bool IsPerishable(this ProductType product)
    {
        return product is ProductType.FreshFood or ProductType.Frozen;
    }

    /// <summary>Goes first on the hub's shelf and the rider's run: express, or food that spoils.</summary>
    public static bool IsPriority(DeliverySpeed speed, ProductType product)
    {
        return speed == DeliverySpeed.Express || product.IsPerishable();
    }
}
