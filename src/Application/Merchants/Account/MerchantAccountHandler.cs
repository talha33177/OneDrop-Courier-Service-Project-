using Microsoft.EntityFrameworkCore;
using Application.Abstractions;
using Application.Merchants.Admin;
using Domain.Common;
using Domain.Delivery;
using Domain.Merchants;
using Domain.Parcels;

namespace Application.Merchants.Account;

public sealed record MerchantAccount(
    MerchantProfile Profile,
    MerchantStatus Status,
    PayoutMethod? PayoutMethod,
    string? PayoutAccount,
    string? PayoutAccountName,
    IReadOnlyList<PickupPointView> PickupPoints);

/// <summary>
/// The signed-in merchant's own settings: its business profile, where payouts go, and its pickup points. The merchant
/// filter keeps every other merchant's rows out of reach.
/// </summary>
public class MerchantAccountHandler(IAppDbContext db, ICurrentUser currentUser)
{
    public static readonly Error PointNotFound = Error.NotFound("pickupPoint.notFound", "That pickup point was not found.");

    public async Task<MerchantAccount> GetAsync(CancellationToken cancellationToken = default)
    {
        var merchant = await MerchantAsync(cancellationToken);
        var points = await (
            from point in db.PickupPoints
            join area in db.Areas on point.AreaId equals area.Id
            where !point.Archived
            orderby point.IsDefault descending, point.Name
            select new PickupPointView(point.Id, point.Name, point.Address, area.Name, area.Id, point.ContactPhone, point.IsDefault))
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        return new MerchantAccount(
            new MerchantProfile(merchant.Name, merchant.OwnerName, merchant.ContactPhone, merchant.ContactEmail, merchant.Address),
            merchant.Status,
            merchant.PayoutMethod,
            merchant.PayoutAccount,
            merchant.PayoutAccountName,
            points);
    }

    public async Task<Result> EditProfileAsync(MerchantProfile profile, CancellationToken cancellationToken = default)
    {
        var merchant = await MerchantAsync(cancellationToken);

        return await SaveAsync(merchant.Edit(profile), cancellationToken);
    }


    /// <summary>Adds a pickup point, or changes one when <paramref name="id"/> is given.</summary>
    public async Task<Result> SavePickupPointAsync(
        long? id,
        long areaId,
        string? name,
        string? address,
        string? phone,
        CancellationToken cancellationToken = default)
    {
        var merchantId = currentUser.MerchantId ?? throw new InvalidOperationException("A merchant has pickup points.");
        if (!await db.Areas.AnyAsync(a => a.Id == areaId && !a.Archived, cancellationToken))
        {
            return Error.Validation("pickupPoint.area", "Choose the pickup point's area.");
        }

        if (id is null)
        {
            var hasDefault = await db.PickupPoints.AnyAsync(p => p.IsDefault && !p.Archived, cancellationToken);
            var created = PickupPoint.Create(merchantId, areaId, name, address, phone, isDefault: !hasDefault);
            if (created.IsFailure)
            {
                return created.Error!;
            }

            db.PickupPoints.Add(created.Value);

            return await SaveAsync(Result.Success(), cancellationToken);
        }

        var point = await db.PickupPoints.SingleOrDefaultAsync(p => p.Id == id && !p.Archived, cancellationToken);
        if (point is null)
        {
            return PointNotFound;
        }

        // Parcels waiting here were priced, and their pickups sent to a hub, by the point's zone
        var zones = await db.Areas
            .Where(a => a.Id == areaId || a.Id == point.AreaId)
            .Select(a => a.ZoneId)
            .Distinct()
            .CountAsync(cancellationToken);
        if (zones > 1 && await HasOpenWorkAsync(point.Id, cancellationToken))
        {
            return Error.Conflict(
                "pickupPoint.busy",
                "Parcels or a pickup are waiting at this point. Move it to another zone once they have been picked up, " +
                "or add a new pickup point for the new place.");
        }

        // A pickup still on the list with nothing to collect was for the old zone's hub: it is called off
        if (zones > 1)
        {
            var empty = await db.PickupRequests
                .Where(r => r.PickupPointId == point.Id && r.Status == PickupStatus.Scheduled)
                .ToListAsync(cancellationToken);
            empty.ForEach(pickup => pickup.Cancel());
        }

        return await SaveAsync(point.Change(areaId, name, address, phone), cancellationToken);
    }

    public async Task<Result> MakeDefaultAsync(long id, CancellationToken cancellationToken = default)
    {
        var points = await db.PickupPoints.Where(p => !p.Archived).ToListAsync(cancellationToken);
        var chosen = points.SingleOrDefault(p => p.Id == id);
        if (chosen is null)
        {
            return PointNotFound;
        }

        // Two saves: the unique index allows one default at a time
        foreach (var point in points.Where(p => p.IsDefault))
        {
            point.MakeDefault(false);
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        chosen.MakeDefault(true);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return Result.Success();
    }

    /// <summary>Parcels wait at the point, or a rider is already on the way to it.</summary>
    private async Task<bool> HasOpenWorkAsync(long pointId, CancellationToken cancellationToken)
    {
        return await db.Parcels.AnyAsync(p => p.PickupPointId == pointId && p.Status == ParcelStatus.Pending, cancellationToken) ||
            await db.PickupRequests.AnyAsync(r => r.PickupPointId == pointId && r.Status == PickupStatus.Assigned, cancellationToken);
    }

    private Task<Merchant> MerchantAsync(CancellationToken cancellationToken)
    {
        var merchantId = currentUser.MerchantId ?? throw new InvalidOperationException("Only a merchant has an account here.");

        return db.Merchants.SingleAsync(m => m.Id == merchantId, cancellationToken);
    }

    private async Task<Result> SaveAsync(Result changed, CancellationToken cancellationToken)
    {
        if (changed.IsFailure)
        {
            return changed;
        }

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            return Error.Conflict("merchant.name.taken", "Another merchant already uses this business name.");
        }

        return Result.Success();
    }
}
