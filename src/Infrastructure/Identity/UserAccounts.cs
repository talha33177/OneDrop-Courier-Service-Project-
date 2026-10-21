using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Application.Abstractions;
using Domain.Common;

namespace Infrastructure.Identity;

/// <summary>
/// Creates logins for the current tenant through Identity, on the same context as the use case, so a use case's
/// transaction covers the login too.
/// </summary>
public class UserAccounts(UserManager<AppUser> users, ITenantContext tenantContext, TimeProvider time) : IUserAccounts
{
    public async Task<LoginInfo?> FindAsync(long userId, CancellationToken cancellationToken = default)
    {
        return await users.Users
            .Where(u => u.Id == userId && u.Email != null)
            .Select(u => new LoginInfo(u.Id, u.Email!, u.DisplayName, u.Archived))
            .SingleOrDefaultAsync(cancellationToken);
    }

    public async Task<LoginInfo?> FindByEmailAsync(string email, CancellationToken cancellationToken = default)
    {
        // The user filter keeps the lookup to this courier; a stopped login is never offered a new password
        var user = await users.FindByNameAsync(email.Trim());

        return user is null || user.Archived || user.Email is null ? null : new LoginInfo(user.Id, user.Email, user.DisplayName, false);
    }

    public async Task<string> PasswordTokenAsync(long userId, CancellationToken cancellationToken = default)
    {
        var user = await users.Users.SingleAsync(u => u.Id == userId, cancellationToken);

        return await users.GeneratePasswordResetTokenAsync(user);
    }

    public async Task<Result> ResetPasswordWithTokenAsync(long userId, string token, string password, CancellationToken cancellationToken = default)
    {
        var user = await users.Users.SingleOrDefaultAsync(u => u.Id == userId, cancellationToken);
        if (user is null || user.Archived)
        {
            return Error.Validation("login.reset", "This link does not work any more. Ask for a new one.");
        }

        var reset = await users.ResetPasswordAsync(user, token, password);
        if (reset.Succeeded)
        {
            return Result.Success();
        }

        return reset.Errors.Any(e => e.Code == nameof(IdentityErrorDescriber.InvalidToken))
            ? Error.Validation("login.reset", "This link has expired or was used already. Ask for a new one.")
            : Error.Validation("login.password", string.Join(" ", reset.Errors.Select(e => e.Description)));
    }

    public async Task<Result<long>> CreateAsync(NewLogin login, CancellationToken cancellationToken = default)
    {
        var tenantId = tenantContext.TenantId ?? throw new InvalidOperationException("A login belongs to a tenant.");
        var email = login.Email?.Trim();
        if (string.IsNullOrEmpty(email) || email.Length > 256 || !email.Contains('@'))
        {
            return Error.Validation("login.email", "Enter a valid email address to sign in with.");
        }

        // The user filter keeps the lookup to this tenant: the same email may sign in at another courier
        if (await users.FindByNameAsync(email) is not null)
        {
            return Error.Conflict("login.email.taken", "This email already has an account here. Sign in instead.");
        }

        var user = new AppUser
        {
            UserName = email,
            Email = email,
            EmailConfirmed = true,
            DisplayName = login.DisplayName,
            TenantId = tenantId,
            MerchantId = login.MerchantId,
            Created = time.GetUtcNow().UtcDateTime
        };
        var created = await users.CreateAsync(user, login.Password ?? "");
        if (created.Succeeded)
        {
            created = await users.AddToRoleAsync(user, login.Role);
        }

        return created.Succeeded
            ? user.Id
            : Error.Validation("login.password", string.Join(" ", created.Errors.Select(e => e.Description)));
    }

    public async Task<IReadOnlyDictionary<long, string>> EmailsAsync(
        IReadOnlyCollection<long> userIds,
        CancellationToken cancellationToken = default)
    {
        return await users.Users
            .Where(u => userIds.Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, u => u.Email ?? u.UserName ?? "", cancellationToken);
    }

    public async Task<IReadOnlyDictionary<long, string>> NamesAsync(
        IReadOnlyCollection<long> userIds,
        CancellationToken cancellationToken = default)
    {
        return await users.Users
            .Where(u => userIds.Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, u => u.DisplayName.Length > 0 ? u.DisplayName : u.Email ?? "", cancellationToken);
    }

    public async Task SetLoginEnabledAsync(long userId, bool enabled, CancellationToken cancellationToken = default)
    {
        if (await users.Users.SingleOrDefaultAsync(u => u.Id == userId, cancellationToken) is not { } user)
        {
            return;
        }

        user.Archived = !enabled;
        await users.SetLockoutEnabledAsync(user, !enabled);
        await users.SetLockoutEndDateAsync(user, enabled ? null : DateTimeOffset.MaxValue);

        // Ends the cookie of a session already open at its next security check
        await users.UpdateSecurityStampAsync(user);
    }

    public async Task<Result> ResetPasswordAsync(long userId, string password, CancellationToken cancellationToken = default)
    {
        if (await users.Users.SingleOrDefaultAsync(u => u.Id == userId, cancellationToken) is not { } user)
        {
            return Error.NotFound("login.notFound", "That login was not found.");
        }

        var token = await users.GeneratePasswordResetTokenAsync(user);
        var reset = await users.ResetPasswordAsync(user, token, password);

        return reset.Succeeded
            ? Result.Success()
            : Error.Validation("login.password", string.Join(" ", reset.Errors.Select(e => e.Description)));
    }

    public async Task<IReadOnlyList<StaffLogin>> InRolesAsync(
        IReadOnlyList<string> roles,
        CancellationToken cancellationToken = default)
    {
        // The user filter keeps every role's list to this courier
        var logins = new List<StaffLogin>();
        foreach (var role in roles)
        {
            logins.AddRange(
                from user in await users.GetUsersInRoleAsync(role)
                where logins.All(l => l.UserId != user.Id)
                select new StaffLogin(user.Id, user.DisplayName, user.Email ?? user.UserName ?? "", role, user.Archived, user.Created));
        }

        return logins;
    }

    public async Task<Result> ChangeAsync(
        long userId,
        string name,
        string from,
        string to,
        CancellationToken cancellationToken = default)
    {
        if (await users.Users.SingleOrDefaultAsync(u => u.Id == userId, cancellationToken) is not { } user)
        {
            return Error.NotFound("login.notFound", "That login was not found.");
        }

        user.DisplayName = name;
        var saved = await users.UpdateAsync(user);
        if (saved.Succeeded && from != to)
        {
            saved = await users.RemoveFromRoleAsync(user, from);
            if (saved.Succeeded)
            {
                saved = await users.AddToRoleAsync(user, to);
            }
        }

        return saved.Succeeded
            ? Result.Success()
            : Error.Validation("login.change", string.Join(" ", saved.Errors.Select(e => e.Description)));
    }

    public async Task<string?> MerchantEmailAsync(long merchantId, CancellationToken cancellationToken = default)
    {
        return await users.Users
            .Where(u => u.MerchantId == merchantId)
            .OrderBy(u => u.Id)
            .Select(u => u.Email)
            .FirstOrDefaultAsync(cancellationToken);
    }
}
