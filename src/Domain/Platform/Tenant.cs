using Domain.Common;

namespace Domain.Platform;

/// <summary>
/// A courier company running on the platform. The only business entity with no TenantId: it is the tenant. Its rate
/// card lives in <c>Pricing.DeliveryRate</c>; every setting here is stated for each tenant explicitly, so nothing falls
/// back to another courier's values.
/// </summary>
public class Tenant : AuditedEntity, IArchivable
{
    private Tenant()
    {
    }

    public string Name { get; private set; } = "";

    /// <summary>Subdomain that selects this tenant: "onedrop" for onedrop.{root domain}.</summary>
    public string Slug { get; private set; } = "";

    /// <summary>IANA or Windows id. Delivery days, runs and payouts are counted in this zone.</summary>
    public string TimeZone { get; private set; } = "";

    public string CurrencyCode { get; private set; } = "";

    /// <summary>Sender name shown on the recipients' SMS.</summary>
    public string SmsSenderName { get; private set; } = "";

    /// <summary>The hotline shown to merchants and recipients.</summary>
    public string SupportPhone { get; private set; } = "";

    /// <summary>
    /// Delivery attempts a parcel gets: a rider may put it on hold for another day until this many attempts have been
    /// made, after which it is returned to the merchant.
    /// </summary>
    public int MaxDeliveryAttempts { get; private set; }

    /// <summary>
    /// The time of day (the tenant's clock) riders are due back at their hub with the day's cash and the parcels they
    /// could not deliver. A run still open after it is late. Null when the courier sets no time.
    /// </summary>
    public TimeOnly? RiderReturnTime { get; private set; }

    /// <summary>
    /// The day's pickup cut-off (the tenant's clock): a parcel booked before it is collected that day, one booked
    /// after it the next day. Null when the courier sets none: a parcel is collected the day it is booked.
    /// </summary>
    public TimeOnly? PickupCutoff { get; private set; }

    /// <summary>When the day's pickup leaves the hub to collect from merchants, as told to them. Null when not said.</summary>
    public TimeOnly? PickupTime { get; private set; }

    /// <summary>How many businesses a merchant account may add besides its main profile. Null when the courier sets no limit.</summary>
    public int? MaxBusinessesPerAccount { get; private set; }

    public bool Archived { get; private set; }
}
