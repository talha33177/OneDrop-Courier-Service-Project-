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
    /// When the day's pickup truck leaves the hub to collect from merchants, as told to them. It visits each pickup
    /// point once: a parcel booked before the truck reaches the point goes with it, one booked after goes the next day.
    /// Null when not said.
    /// </summary>
    public TimeOnly? PickupTime { get; private set; }

    /// <summary>How many businesses a merchant account may add besides its main profile. Null when the courier sets no limit.</summary>
    public int? MaxBusinessesPerAccount { get; private set; }

    /// <summary>
    /// Days a parcel may stay where it is before the admin is told it is stuck. Null when the courier watches for none.
    /// </summary>
    public int? StuckAfterDays { get; private set; }

    public bool Archived { get; private set; }

    public const int MaxStuckAfterDays = 60;

    public const int MaxSmsSenderLength = 11;

    public const int MaxAttempts = 10;

    public const int MaxBusinesses = 100;

    /// <summary>
    /// The settings the courier's own admin keeps. The slug, time zone and currency are not among them: changing them
    /// would move the courier's address, its days or its money under parcels already on their way.
    /// </summary>
    public Result ChangeSettings(TenantSettings settings)
    {
        var name = settings.Name.NullIfBlank();
        if (name is null || name.Length > 200)
        {
            return Error.Validation("tenant.name", "Name the courier, at most 200 characters.");
        }

        var phone = settings.SupportPhone.NullIfBlank();
        if (phone is null || phone.Length > 20 || !phone.Any(char.IsDigit) || phone.Any(c => !char.IsDigit(c) && c is not ('+' or '-' or ' ')))
        {
            return Error.Validation("tenant.supportPhone", "Enter the hotline in digits, such as 09610-001122, at most 20 characters.");
        }

        // The phone networks show at most 11 letters and digits as the sender of a text
        var sender = settings.SmsSenderName.NullIfBlank();
        if (sender is null || sender.Length > MaxSmsSenderLength || !sender.All(char.IsAsciiLetterOrDigit))
        {
            return Error.Validation("tenant.smsSender", $"The SMS sender name is letters and digits only, at most {MaxSmsSenderLength}, such as OneDrop.");
        }

        if (settings.MaxDeliveryAttempts is < 1 or > MaxAttempts)
        {
            return Error.Validation("tenant.attempts", $"A parcel gets between 1 and {MaxAttempts} delivery attempts.");
        }

        if (settings.MaxBusinessesPerAccount is < 1 or > MaxBusinesses)
        {
            return Error.Validation("tenant.businesses", $"Allow between 1 and {MaxBusinesses} businesses per account, or leave it empty for no limit.");
        }

        if (settings.StuckAfterDays is < 1 or > MaxStuckAfterDays)
        {
            return Error.Validation("tenant.stuck", $"Call a parcel stuck after between 1 and {MaxStuckAfterDays} days, or leave it empty to watch for none.");
        }

        Name = name;
        SupportPhone = phone;
        SmsSenderName = sender;
        MaxDeliveryAttempts = settings.MaxDeliveryAttempts;
        MaxBusinessesPerAccount = settings.MaxBusinessesPerAccount;
        RiderReturnTime = settings.RiderReturnTime;
        PickupTime = settings.PickupTime;
        StuckAfterDays = settings.StuckAfterDays;

        return Result.Success();
    }
}

/// <summary>What the courier's admin may change about the courier; see <see cref="Tenant.ChangeSettings"/>.</summary>
public sealed record TenantSettings(
    string? Name,
    string? SupportPhone,
    string? SmsSenderName,
    int MaxDeliveryAttempts,
    int? MaxBusinessesPerAccount,
    TimeOnly? RiderReturnTime,
    TimeOnly? PickupTime,
    int? StuckAfterDays);
