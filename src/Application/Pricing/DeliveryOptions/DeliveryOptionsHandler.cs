using Microsoft.EntityFrameworkCore;
using Application.Abstractions;
using Domain.Common;
using Domain.Pricing;

namespace Application.Pricing.DeliveryOptions;

/// <summary>What an option adds in one service area, and for a speed the days after pickup (null: the rate card's).</summary>
public sealed record OptionCharge(ServiceArea ServiceArea, decimal Charge, int? DeliveryDays);

/// <summary>A delivery speed or kind of contents as the admin, the booking form and the price lists show it.</summary>
public sealed record OptionRow(
    long Id,
    DeliveryOptionKind Kind,
    string Code,
    string Name,
    string? Description,
    string? Handling,
    string Icon,
    bool GoesFirst,
    bool IsDefault,
    bool Archived,
    IReadOnlyList<OptionCharge> Charges)
{
    /// <summary>What it adds in <paramref name="area"/>: 0 for the default, null where it is not offered.</summary>
    public decimal? ChargeIn(ServiceArea area)
    {
        return IsDefault ? 0 : Charges.FirstOrDefault(c => c.ServiceArea == area)?.Charge;
    }

    /// <summary>Its own delivery time in <paramref name="area"/>, or null for the rate card's.</summary>
    public int? DaysIn(ServiceArea area)
    {
        return Charges.FirstOrDefault(c => c.ServiceArea == area)?.DeliveryDays;
    }
}

/// <summary>What the admin asks an option to cost in one service area; an empty charge stops it being offered there.</summary>
public sealed record OptionPriceInput(ServiceArea ServiceArea, decimal? Charge, int? DeliveryDays);

/// <summary>
/// The courier's delivery speeds and kinds of contents (<see cref="DeliveryOption"/>): listed for the booking form and
/// the price lists, and added, changed, reordered and taken off by its admin. A change prices parcels booked from
/// then on; a parcel keeps the option and the extra it was booked with.
/// </summary>
public class DeliveryOptionsHandler(IAppDbContext db)
{
    /// <summary>Options of one kind on sale at once, so the booking form stays readable.</summary>
    public const int MaxPerKind = 12;

    public static readonly Error NotFound = Error.NotFound("option.notFound", "That delivery type was not found.");

    /// <summary>Every option, each kind in its order; those no longer sold only when <paramref name="withArchived"/>.</summary>
    public async Task<IReadOnlyList<OptionRow>> ListAsync(bool withArchived = false, CancellationToken cancellationToken = default)
    {
        var options = await db.DeliveryOptions
            .Include(o => o.Charges)
            .Where(o => withArchived || !o.Archived)
            .OrderBy(o => o.Kind)
            .ThenBy(o => o.Archived)
            .ThenBy(o => o.SortOrder)
            .ThenBy(o => o.Id)
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        return
        [
            .. options.Select(o => new OptionRow(
                o.Id,
                o.Kind,
                o.Code,
                o.Name,
                o.Description,
                o.Handling,
                o.Icon,
                o.GoesFirst,
                o.IsDefault,
                o.Archived,
                [.. o.Charges.OrderBy(c => c.ServiceArea).Select(c => new OptionCharge(c.ServiceArea, c.Charge, c.DeliveryDays))]))
        ];
    }

    /// <summary>A new speed or kind of contents, last of its kind, with its code made from its name.</summary>
    public async Task<Result<string>> AddAsync(
        DeliveryOptionKind kind,
        DeliveryOptionValues values,
        IReadOnlyList<OptionPriceInput> prices,
        CancellationToken cancellationToken = default)
    {
        var ofKind = await db.DeliveryOptions.Where(o => o.Kind == kind).ToListAsync(cancellationToken);
        if (ofKind.Count(o => !o.Archived) >= MaxPerKind)
        {
            return Error.Conflict(
                "option.tooMany",
                $"Up to {MaxPerKind} of each are sold at once. Take one off before adding another.");
        }

        var named = NameTaken(ofKind, values.Name, null);
        if (named is not null)
        {
            return named;
        }

        var stem = DeliveryOption.MakeCode(values.Name ?? "");
        if (stem.Length == 0)
        {
            return Error.Validation("option.name", "Give it a name with letters or digits, such as \"Same day\".");
        }

        var code = stem;
        for (var n = 2; ofKind.Any(o => o.Code == code); n++)
        {
            code = $"{stem[..Math.Min(stem.Length, DeliveryOption.MaxCodeLength - 4)]}-{n}";
        }

        var created = DeliveryOption.Create(kind, code, values, ofKind.Select(o => o.SortOrder).DefaultIfEmpty().Max() + 1);
        if (created.IsFailure)
        {
            return created.Error!;
        }

        var priced = Price(created.Value, prices);
        if (priced.IsFailure)
        {
            return priced.Error!;
        }

        db.DeliveryOptions.Add(created.Value);
        await db.SaveChangesAsync(cancellationToken);

        return created.Value.Name;
    }

    /// <summary>A new name, description, handling and price list; the code stays, so integrations keep working.</summary>
    public async Task<Result<string>> ChangeAsync(
        long id,
        DeliveryOptionValues values,
        IReadOnlyList<OptionPriceInput> prices,
        CancellationToken cancellationToken = default)
    {
        var option = await FindAsync(id, cancellationToken);
        if (option is null)
        {
            return NotFound;
        }

        var ofKind = await db.DeliveryOptions.Where(o => o.Kind == option.Kind).ToListAsync(cancellationToken);
        var named = NameTaken(ofKind, values.Name, option.Id);
        if (named is not null)
        {
            return named;
        }

        var changed = option.Change(values);
        if (changed.IsFailure)
        {
            return changed.Error!;
        }

        var priced = Price(option, prices);
        if (priced.IsFailure)
        {
            return priced.Error!;
        }

        await db.SaveChangesAsync(cancellationToken);

        return option.Name;
    }

    /// <summary>Swaps the option with the one before (<paramref name="up"/>) or after it among those on sale.</summary>
    public async Task<Result> MoveAsync(long id, bool up, CancellationToken cancellationToken = default)
    {
        var option = await FindAsync(id, cancellationToken);
        if (option is null)
        {
            return NotFound;
        }

        var ordered = await db.DeliveryOptions
            .Where(o => o.Kind == option.Kind && !o.Archived)
            .OrderBy(o => o.SortOrder)
            .ThenBy(o => o.Id)
            .ToListAsync(cancellationToken);
        var at = ordered.FindIndex(o => o.Id == id);
        var to = up ? at - 1 : at + 1;
        if (at < 0 || to < 0 || to >= ordered.Count)
        {
            return Result.Success();
        }

        (ordered[at], ordered[to]) = (ordered[to], ordered[at]);
        for (var i = 0; i < ordered.Count; i++)
        {
            ordered[i].MoveTo(i + 1);
        }

        await db.SaveChangesAsync(cancellationToken);

        return Result.Success();
    }

    /// <summary>
    /// Makes the option what a merchant gets when choosing nothing: offered everywhere at no extra, in the rate
    /// card's time. The old default keeps no prices, so it is offered nowhere until the admin prices it again.
    /// </summary>
    public async Task<Result<string>> MakeDefaultAsync(long id, CancellationToken cancellationToken = default)
    {
        var option = await FindAsync(id, cancellationToken);
        if (option is null)
        {
            return NotFound;
        }

        var old = await db.DeliveryOptions.SingleOrDefaultAsync(o => o.Kind == option.Kind && o.IsDefault, cancellationToken);
        if (old?.Id == option.Id)
        {
            return option.Name;
        }

        if (option.Archived)
        {
            return Error.Conflict("option.archived", $"{option.Name} is no longer sold. Bring it back first.");
        }

        // The database keeps one default per kind, so the old one lets go in a save of its own, in one transaction
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        if (old is not null)
        {
            old.ClearDefault();
            await db.SaveChangesAsync(cancellationToken);
        }

        option.MakeDefault();
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return option.Name;
    }

    /// <summary>Stops selling the option; parcels booked with it keep it. The default cannot be taken off.</summary>
    public async Task<Result<string>> ArchiveAsync(long id, CancellationToken cancellationToken = default)
    {
        var option = await FindAsync(id, cancellationToken);
        if (option is null)
        {
            return NotFound;
        }

        var archived = option.Archive();
        if (archived.IsFailure)
        {
            return archived.Error!;
        }

        await db.SaveChangesAsync(cancellationToken);

        return option.Name;
    }

    /// <summary>Sells the option again, last of its kind, with the prices it had.</summary>
    public async Task<Result<string>> RestoreAsync(long id, CancellationToken cancellationToken = default)
    {
        var option = await FindAsync(id, cancellationToken);
        if (option is null)
        {
            return NotFound;
        }

        var ofKind = await db.DeliveryOptions.Where(o => o.Kind == option.Kind && !o.Archived).ToListAsync(cancellationToken);
        if (ofKind.Count >= MaxPerKind)
        {
            return Error.Conflict("option.tooMany", $"Up to {MaxPerKind} of each are sold at once. Take one off first.");
        }

        var named = NameTaken(ofKind, option.Name, option.Id);
        if (named is not null)
        {
            return named;
        }

        option.Restore();
        option.MoveTo(ofKind.Select(o => o.SortOrder).DefaultIfEmpty().Max() + 1);
        await db.SaveChangesAsync(cancellationToken);

        return option.Name;
    }

    private Task<DeliveryOption?> FindAsync(long id, CancellationToken cancellationToken)
    {
        return db.DeliveryOptions.Include(o => o.Charges).SingleOrDefaultAsync(o => o.Id == id, cancellationToken);
    }

    /// <summary>Two options of a kind on sale under one name would be one card twice on the booking form.</summary>
    private static Error? NameTaken(IEnumerable<DeliveryOption> ofKind, string? name, long? except)
    {
        var trimmed = name?.Trim();
        var taken = ofKind.FirstOrDefault(o =>
            !o.Archived && o.Id != except && string.Equals(o.Name, trimmed, StringComparison.OrdinalIgnoreCase));

        return taken is null
            ? null
            : Error.Conflict("option.name.taken", $"There is already a {taken.KindName} called {taken.Name}.");
    }

    private static Result Price(DeliveryOption option, IReadOnlyList<OptionPriceInput> prices)
    {
        foreach (var price in prices)
        {
            var set = option.SetCharge(price.ServiceArea, price.Charge, price.DeliveryDays);
            if (set.IsFailure)
            {
                return set;
            }
        }

        return Result.Success();
    }
}
