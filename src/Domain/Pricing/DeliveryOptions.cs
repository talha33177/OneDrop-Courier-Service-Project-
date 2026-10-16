using Domain.Common;

namespace Domain.Pricing;

/// <summary>The two choices a merchant makes when booking. Stored as TINYINT. Never renumber a value that has been saved.</summary>
public enum DeliveryOptionKind : byte
{
    /// <summary>How fast it goes: regular, express, and whatever else the courier sells.</summary>
    Speed = 1,

    /// <summary>What is inside, which decides how it is handled: general goods, liquid, fragile, frozen and so on.</summary>
    Contents = 2
}

/// <summary>
/// A delivery speed or a kind of contents the courier sells, made and changed by its admin. Each kind has one default
/// (<see cref="IsDefault"/>: regular, general goods) that every route takes at no extra, for a merchant who chooses
/// nothing. Every other option costs its <see cref="Charges"/> extra in each service area that offers it; a service
/// area with no charge does not take it. A speed may promise its own delivery time there. An option that
/// <see cref="GoesFirst"/> is sorted first on the hub's shelf and the rider's run (express, food that spoils).
/// <see cref="Code"/> is how the API and the CSV upload name it; it is made from the first name and never changes, so
/// a merchant's integration keeps working when the option is renamed. An option the courier stops selling is archived,
/// never deleted: the parcels booked with it keep it.
/// </summary>
public class DeliveryOption : TenantEntity, IArchivable
{
    public const int MaxNameLength = 40;
    public const int MaxDescriptionLength = 120;
    public const int MaxHandlingLength = 80;
    public const int MaxCodeLength = 30;
    public const int MaxIconLength = 30;

    private readonly List<DeliveryOptionCharge> charges = [];

    private DeliveryOption()
    {
    }

    public DeliveryOptionKind Kind { get; private set; }

    public string Code { get; private set; } = "";

    public string Name { get; private set; } = "";

    /// <summary>One line under the name on the booking form ("Glass, ceramics, screens").</summary>
    public string? Description { get; private set; }

    /// <summary>What hubs and riders are told on labels and runs ("Fragile: handle with care"); contents only.</summary>
    public string? Handling { get; private set; }

    /// <summary>The icon's name, from the set the admin picks from.</summary>
    public string Icon { get; private set; } = "";

    public bool GoesFirst { get; private set; }

    public bool IsDefault { get; private set; }

    /// <summary>Where it stands among its kind on the booking form, lowest first.</summary>
    public int SortOrder { get; private set; }

    public bool Archived { get; private set; }

    public IReadOnlyList<DeliveryOptionCharge> Charges => charges;

    public string KindName => Kind == DeliveryOptionKind.Speed ? "delivery speed" : "kind of contents";

    public static Result<DeliveryOption> Create(DeliveryOptionKind kind, string code, DeliveryOptionValues values, int sortOrder)
    {
        if (!Enum.IsDefined(kind))
        {
            return Error.Validation("option.kind", "Choose a delivery speed or a kind of contents.");
        }

        if (code.Length is 0 or > MaxCodeLength || code != MakeCode(code))
        {
            return Error.Validation("option.code", "The code is made of small letters, digits and hyphens.");
        }

        var option = new DeliveryOption { Kind = kind, Code = code, SortOrder = sortOrder };
        var changed = option.Change(values);

        return changed.IsSuccess ? option : changed.Error!;
    }

    /// <summary>A new name, description, handling, icon and place in the queue; the code stays.</summary>
    public Result Change(DeliveryOptionValues values)
    {
        var name = values.Name.NullIfBlank();
        if (name is null || name.Length > MaxNameLength)
        {
            return Error.Validation("option.name", $"Name it, such as \"Fragile\", at most {MaxNameLength} characters.");
        }

        var description = values.Description.NullIfBlank();
        if (description?.Length > MaxDescriptionLength)
        {
            return Error.Validation("option.description", $"Keep the description to {MaxDescriptionLength} characters.");
        }

        var handling = values.Handling.NullIfBlank();
        if (handling?.Length > MaxHandlingLength)
        {
            return Error.Validation("option.handling", $"Keep the handling note to {MaxHandlingLength} characters.");
        }

        var icon = values.Icon.NullIfBlank();
        if (icon is null || icon.Length > MaxIconLength || !icon.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '-'))
        {
            return Error.Validation("option.icon", "Choose an icon.");
        }

        Name = name;
        Description = description;
        Handling = Kind == DeliveryOptionKind.Contents ? handling : null;
        Icon = icon;
        GoesFirst = values.GoesFirst;

        return Result.Success();
    }

    /// <summary>
    /// What it adds in <paramref name="area"/> and, for a speed, the days after pickup it is delivered in (null keeps
    /// the rate card's time). A null charge stops it being offered there. The default always goes at no extra.
    /// </summary>
    public Result SetCharge(ServiceArea area, decimal? charge, int? days)
    {
        if (!Enum.IsDefined(area))
        {
            return Error.Validation("option.area", "Choose a service area.");
        }

        if (IsDefault)
        {
            return charge is null or 0 && days is null
                ? Result.Success()
                : Error.Validation("option.default", $"{Name} is the default: it goes everywhere at no extra, in the rate card's time.");
        }

        if (charge is < 0 or > DeliveryRate.MaxCharge)
        {
            return Error.Validation("option.charge", $"Extras are between ৳0 and ৳{DeliveryRate.MaxCharge:N0}, or left empty where it is not offered.");
        }

        if (days is not null && (Kind != DeliveryOptionKind.Speed || charge is null))
        {
            return Error.Validation("option.days", "Only a delivery speed promises a time, and only where it is offered.");
        }

        if (days is < 0 or > DeliveryRate.MaxDeliveryDays)
        {
            return Error.Validation(
                "option.days",
                $"The delivery time is between 0 days (the same day) and {DeliveryRate.MaxDeliveryDays}, or empty for the rate card's time.");
        }

        var existing = charges.FirstOrDefault(c => c.ServiceArea == area);
        if (charge is null)
        {
            if (existing is not null)
            {
                charges.Remove(existing);
            }
        }
        else if (existing is null)
        {
            charges.Add(new DeliveryOptionCharge(area, charge.Value, days));
        }
        else
        {
            existing.Change(charge.Value, days);
        }

        return Result.Success();
    }

    /// <summary>What it adds in <paramref name="area"/>: 0 for the default, null where it is not offered.</summary>
    public decimal? ChargeIn(ServiceArea area)
    {
        return IsDefault ? 0 : charges.FirstOrDefault(c => c.ServiceArea == area)?.Charge;
    }

    /// <summary>The days after pickup a parcel going at this speed is delivered in, or null for the rate card's time.</summary>
    public int? DaysIn(ServiceArea area)
    {
        return IsDefault ? null : charges.FirstOrDefault(c => c.ServiceArea == area)?.DeliveryDays;
    }

    public void MoveTo(int sortOrder)
    {
        SortOrder = sortOrder;
    }

    /// <summary>Makes this the default of its kind; the caller takes the flag off the old one.</summary>
    public Result MakeDefault()
    {
        if (Archived)
        {
            return Error.Conflict("option.archived", $"{Name} is no longer sold. Bring it back first.");
        }

        IsDefault = true;
        charges.Clear();

        return Result.Success();
    }

    public void ClearDefault()
    {
        IsDefault = false;
    }

    /// <summary>The courier stops selling it: it leaves the booking form and the API, parcels booked with it keep it.</summary>
    public Result Archive()
    {
        if (IsDefault)
        {
            return Error.Conflict(
                "option.default",
                $"{Name} is what a merchant gets when choosing nothing, so it cannot be taken off. Make another {KindName} the default first.");
        }

        Archived = true;

        return Result.Success();
    }

    public void Restore()
    {
        Archived = false;
    }

    /// <summary>
    /// The code an option's name gives: small letters and digits, other characters as single hyphens ("Fresh food" →
    /// "fresh-food"), at most <see cref="MaxCodeLength"/> characters.
    /// </summary>
    public static string MakeCode(string name)
    {
        var code = new System.Text.StringBuilder();
        foreach (var c in name.Trim().ToLowerInvariant())
        {
            if (char.IsAsciiLetterOrDigit(c))
            {
                code.Append(c);
            }
            else if (code.Length > 0 && code[^1] != '-')
            {
                code.Append('-');
            }
        }

        var made = code.ToString().Trim('-');

        return made.Length > MaxCodeLength ? made[..MaxCodeLength].TrimEnd('-') : made;
    }

    /// <summary>
    /// Whether a merchant's <paramref name="text"/> names this option, by code or name, ignoring case, spaces, hyphens
    /// and underscores: "fresh-food", "Fresh food", "fresh_food" and "freshFood" all name fresh food.
    /// </summary>
    public bool IsNamedBy(string text)
    {
        var wanted = Squash(text);

        return wanted.Length > 0 && (wanted == Squash(Code) || wanted == Squash(Name));
    }

    private static string Squash(string text)
    {
        return string.Concat(text.Where(char.IsLetterOrDigit)).ToLowerInvariant();
    }
}

/// <summary>What a <see cref="DeliveryOption"/> adds in one service area, and for a speed how soon it is delivered there.</summary>
public class DeliveryOptionCharge : TenantEntity
{
    private DeliveryOptionCharge()
    {
    }

    internal DeliveryOptionCharge(ServiceArea area, decimal charge, int? days)
    {
        ServiceArea = area;
        Change(charge, days);
    }

    public long DeliveryOptionId { get; private set; }

    public ServiceArea ServiceArea { get; private set; }

    public decimal Charge { get; private set; }

    /// <summary>Days after pickup, for a speed; null keeps the rate card's time.</summary>
    public int? DeliveryDays { get; private set; }

    internal void Change(decimal charge, int? days)
    {
        Charge = charge;
        DeliveryDays = days;
    }
}

/// <summary>An option's name and how it is shown and handled.</summary>
public sealed record DeliveryOptionValues(string? Name, string? Description, string? Handling, string? Icon, bool GoesFirst);
