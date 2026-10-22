using Microsoft.EntityFrameworkCore;
using Application.Abstractions;
using Application.Common;
using Domain.Common;
using Domain.Merchants;
using Domain.Parcels;

namespace Application.Merchants.Businesses;

/// <summary>One business of the merchant's account as its card shows it: its own parcels on the way and money waiting.</summary>
public sealed record BusinessCard(long Id, string Name, bool IsMain, MerchantStatus Status, int OnTheWay, decimal Unpaid, DateTime Since, DateTime? Picture);

/// <summary>The business's name and, when it has a picture, when it last changed (so a new one is fetched again).</summary>
public sealed record BusinessItem(long Id, string Name, bool IsMain, DateTime? Picture);

/// <summary>How many businesses an account has added besides its main profile, out of the courier's most (null: no limit).</summary>
public sealed record BusinessRoom(int Added, int? Limit)
{
    public bool Full => Limit is { } most && Added >= most;
}

/// <summary>Another business for the account: its name and contact, and where its parcels are picked up.</summary>
public sealed record NewBusiness(
    string? Name,
    string? Phone,
    string? Email,
    string? Address,
    long? PickupAreaId,
    string? PickupAddress);

/// <summary>
/// The businesses of a merchant's account. Each is a merchant of its own (parcels, pickups, payments, balance, API keys
/// and webhook), and the account's logins work in any of them, one at a time. Reading them lifts the merchant filter,
/// which otherwise holds the login to the business it is working in, and keeps to the login's own account.
/// </summary>
public class MerchantBusinessesHandler(IAppDbContext db, ICurrentUser currentUser, ITenantContext tenantContext)
{
    public static readonly Error NotFound = Error.NotFound("business.notFound", "That business was not found.");

    public async Task<IReadOnlyList<BusinessCard>> ListAsync(CancellationToken cancellationToken = default)
    {
        var account = Account();
        var parcels = db.Parcels.IgnoreQueryFilters([QueryFilters.Merchant]);
        var lines = db.LedgerEntries.IgnoreQueryFilters([QueryFilters.Merchant]);
        var pictures = db.MerchantPictures.IgnoreQueryFilters([QueryFilters.Merchant]);

        return await db.Merchants
            .IgnoreQueryFilters([QueryFilters.Merchant])
            .Where(m => m.Id == account || m.MainMerchantId == account)
            .OrderBy(m => m.MainMerchantId != null)
            .ThenBy(m => m.Name)
            .Select(m => new BusinessCard(
                m.Id,
                m.Name,
                m.MainMerchantId == null,
                m.Status,
                parcels.Count(p => p.MerchantId == m.Id &&
                    (p.Status == ParcelStatus.Pending || p.Status == ParcelStatus.PickedUp || p.Status == ParcelStatus.AtHub ||
                     p.Status == ParcelStatus.InTransit || p.Status == ParcelStatus.OutForDelivery || p.Status == ParcelStatus.OnHold)),
                lines.Where(e => e.MerchantId == m.Id && e.PayoutId == null).Sum(e => (decimal?)e.Amount) ?? 0,
                m.Created,
                pictures.Where(p => p.MerchantId == m.Id).Select(p => (DateTime?)p.UpdatedOn).FirstOrDefault()))
            .AsNoTracking()
            .ToListAsync(cancellationToken);
    }

    /// <summary>How many businesses the account has added besides its main profile, and the courier's most.</summary>
    public async Task<BusinessRoom> RoomAsync(CancellationToken cancellationToken = default)
    {
        var account = Account();
        var added = await db.Merchants
            .IgnoreQueryFilters([QueryFilters.Merchant])
            .CountAsync(m => m.MainMerchantId == account && !m.Archived, cancellationToken);

        return new BusinessRoom(added, tenantContext.Require().MaxBusinessesPerAccount);
    }

    /// <summary>The account's businesses by name only, main profile first: for the switcher on every page.</summary>
    public async Task<IReadOnlyList<BusinessItem>> NamesAsync(CancellationToken cancellationToken = default)
    {
        var account = Account();
        var pictures = db.MerchantPictures.IgnoreQueryFilters([QueryFilters.Merchant]);

        return await db.Merchants
            .IgnoreQueryFilters([QueryFilters.Merchant])
            .Where(m => m.Id == account || m.MainMerchantId == account)
            .OrderBy(m => m.MainMerchantId != null)
            .ThenBy(m => m.Name)
            .Select(m => new BusinessItem(
                m.Id,
                m.Name,
                m.MainMerchantId == null,
                pictures.Where(p => p.MerchantId == m.Id).Select(p => (DateTime?)p.UpdatedOn).FirstOrDefault()))
            .AsNoTracking()
            .ToListAsync(cancellationToken);
    }

    /// <summary>The business's name, if it is one of this login's account; not found for anyone else's.</summary>
    public async Task<Result<string>> NameOfAsync(long id, CancellationToken cancellationToken = default)
    {
        var account = Account();
        var name = await db.Merchants
            .IgnoreQueryFilters([QueryFilters.Merchant])
            .Where(m => m.Id == id && (m.Id == account || m.MainMerchantId == account))
            .Select(m => m.Name)
            .SingleOrDefaultAsync(cancellationToken);

        return name is null ? NotFound : name;
    }

    /// <summary>Adds a business to the account with its default pickup point, in one transaction.</summary>
    public async Task<Result<long>> AddAsync(NewBusiness spec, CancellationToken cancellationToken = default)
    {
        var account = Account();
        var merchants = db.Merchants.IgnoreQueryFilters([QueryFilters.Merchant]);
        var main = await merchants.SingleAsync(m => m.Id == account, cancellationToken);
        var room = await RoomAsync(cancellationToken);
        var business = Merchant.AddBusiness(
            main,
            new MerchantProfile(spec.Name, main.OwnerName, spec.Phone, spec.Email, spec.Address),
            room.Added,
            room.Limit);
        if (business.IsFailure)
        {
            return business.Error!;
        }

        if (await merchants.AnyAsync(m => m.Name == business.Value.Name, cancellationToken))
        {
            return Error.Conflict("merchant.name.taken", "A business with this name already sends parcels with us. Choose another name.");
        }

        var area = spec.PickupAreaId is { } areaId
            ? await db.Areas.SingleOrDefaultAsync(a => a.Id == areaId && !a.Archived, cancellationToken)
            : null;
        if (area is null)
        {
            return Error.Validation("merchant.pickup.area", "Choose the area this business's parcels are picked up from.");
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        db.Merchants.Add(business.Value);
        await db.SaveChangesAsync(cancellationToken);

        var point = PickupPoint.Create(
            business.Value.Id,
            area.Id,
            "Main pickup point",
            spec.PickupAddress,
            business.Value.ContactPhone,
            isDefault: true);
        if (point.IsFailure)
        {
            return point.Error!;
        }

        db.PickupPoints.Add(point.Value);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return business.Value.Id;
    }

    private long Account()
    {
        return currentUser.AccountId ?? throw new InvalidOperationException("Only a merchant login has businesses.");
    }
}
