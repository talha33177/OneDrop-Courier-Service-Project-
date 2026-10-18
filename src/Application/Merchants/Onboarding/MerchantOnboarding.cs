using Microsoft.EntityFrameworkCore;
using Application.Abstractions;
using Application.Common;
using Domain.Common;
using Domain.Merchants;

namespace Application.Merchants.Onboarding;

/// <summary>
/// A new merchant account: the business, its login and its first (default) pickup point. Used by the public sign-up
/// form and by the admin's "Add merchant".
/// </summary>
public sealed record NewMerchant(
    MerchantProfile Profile,
    string? Email,
    string? Password,
    long? PickupAreaId,
    string? PickupAddress);

/// <summary>
/// Creates a merchant with its login and default pickup point in one transaction, so a refused login (email taken, weak
/// password) leaves no merchant behind. A signed-up merchant waits for approval; one the admin adds is active at once.
/// </summary>
public class MerchantOnboarding(IAppDbContext db, IUserAccounts accounts)
{
    /// <summary>
    /// What a sign-up would be refused for, found before a code is emailed for it: the business's details, a name
    /// already used, the pickup area, an email that already has a login here. The password is checked when it is made.
    /// </summary>
    public async Task<Result> CheckAsync(NewMerchant spec, CancellationToken cancellationToken = default)
    {
        var merchant = Merchant.Add(spec.Profile);
        if (merchant.IsFailure)
        {
            return merchant.Error!;
        }

        if (await db.Merchants.AnyAsync(m => m.Name == merchant.Value.Name, cancellationToken))
        {
            return Error.Conflict("merchant.name.taken", "A merchant with this business name already exists.");
        }

        if (spec.PickupAreaId is not { } areaId || !await db.Areas.AnyAsync(a => a.Id == areaId && !a.Archived, cancellationToken))
        {
            return Error.Validation("merchant.pickup.area", "Choose the area your parcels are picked up from.");
        }

        return spec.Email is { } email && await accounts.FindByEmailAsync(email, cancellationToken) is not null
            ? Error.Conflict("login.email.taken", "This email already has an account here. Sign in instead.")
            : Result.Success();
    }

    public async Task<Result<long>> CreateAsync(NewMerchant spec, bool approved, CancellationToken cancellationToken = default)
    {
        var merchant = approved ? Merchant.Add(spec.Profile) : Merchant.SignUp(spec.Profile);
        if (merchant.IsFailure)
        {
            return merchant.Error!;
        }

        if (await db.Merchants.AnyAsync(m => m.Name == merchant.Value.Name, cancellationToken))
        {
            return Error.Conflict("merchant.name.taken", "A merchant with this business name already exists.");
        }

        var area = spec.PickupAreaId is { } areaId
            ? await db.Areas.SingleOrDefaultAsync(a => a.Id == areaId && !a.Archived, cancellationToken)
            : null;
        if (area is null)
        {
            return Error.Validation("merchant.pickup.area", "Choose the area your parcels are picked up from.");
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        db.Merchants.Add(merchant.Value);
        await db.SaveChangesAsync(cancellationToken);

        var point = PickupPoint.Create(
            merchant.Value.Id,
            area.Id,
            "Main pickup point",
            spec.PickupAddress,
            merchant.Value.ContactPhone,
            isDefault: true);
        if (point.IsFailure)
        {
            return point.Error!;
        }

        db.PickupPoints.Add(point.Value);
        await db.SaveChangesAsync(cancellationToken);

        var login = await accounts.CreateAsync(
            new NewLogin(spec.Email, spec.Password, merchant.Value.OwnerName, Roles.Merchant) { MerchantId = merchant.Value.Id },
            cancellationToken);
        if (login.IsFailure)
        {
            return login.Error!;
        }

        await transaction.CommitAsync(cancellationToken);

        return merchant.Value.Id;
    }
}
