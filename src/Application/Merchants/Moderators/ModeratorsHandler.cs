using Microsoft.EntityFrameworkCore;
using Application.Abstractions;
using Application.Common;
using Application.Notifications;
using Domain.Common;
using Domain.Merchants;

namespace Application.Merchants.Moderators;

/// <summary>One of the account's moderators as the owner sees them. The password is never shown again after it is set.</summary>
public sealed record ModeratorRow(
    long Id,
    string Name,
    string Email,
    string? Phone,
    MerchantPermissions Permissions,
    bool Stopped,
    DateTime Added);

/// <summary>Someone to add: their name, the email they will sign in with, their phone and what they may do.</summary>
public sealed record NewModerator(string? Name, string? Email, string? Phone, MerchantPermissions Permissions);

/// <summary>
/// The moderators of the signed-in merchant account: people who work in the account with a sign-in of their own. Only
/// the account's owner manages them, and only on the account's main profile, so a moderator is the same person in
/// every business of the account. We send no email yet, so adding one returns a first password to hand over; the
/// person changes it at /Account/Password.
/// </summary>
public class ModeratorsHandler(
    IAppDbContext db,
    ICurrentUser currentUser,
    IUserAccounts accounts,
    MerchantAccess access,
    ITenantContext tenantContext)
{
    public static readonly Error NotFound = Error.NotFound("moderator.notFound", "That person was not found.");

    public static readonly Error NotTheOwner = Error.Forbidden(
        "moderator.notTheOwner",
        "Only the account's owner adds moderators and changes what they may do.");

    public async Task<IReadOnlyList<ModeratorRow>> ListAsync(CancellationToken cancellationToken = default)
    {
        var moderators = Account() is { } accountId
            ? await db.Moderators
                .Where(m => m.AccountId == accountId)
                .OrderBy(m => m.Archived)
                .ThenBy(m => m.Name)
                .AsNoTracking()
                .ToListAsync(cancellationToken)
            : [];
        if (moderators.Count == 0)
        {
            return [];
        }

        var emails = await accounts.EmailsAsync([.. moderators.Select(m => m.UserId)], cancellationToken);
        var timeZone = TimeZoneInfo.FindSystemTimeZoneById(
            tenantContext.Tenant?.TimeZone ?? TimeZoneInfo.Utc.Id);

        return
        [
            .. moderators.Select(m => new ModeratorRow(
                m.Id,
                m.Name,
                emails.TryGetValue(m.UserId, out var email) ? email : "",
                m.Phone,
                m.Permissions,
                m.Archived,
                TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(m.Created, DateTimeKind.Utc), timeZone)))
        ];
    }

    /// <summary>Adds a moderator with a login of their own. The password in the result is shown once, to hand over.</summary>
    public async Task<Result<string>> AddAsync(NewModerator spec, CancellationToken cancellationToken = default)
    {
        var rights = await access.CurrentAsync(cancellationToken);
        if (!rights.MayManageModerators)
        {
            return NotTheOwner;
        }

        if (Account() is not { } accountId)
        {
            return NotFound;
        }

        var phone = spec.Phone.NullIfBlank() is { } given ? PhoneNumber.Parse(given) : null;
        if (phone is { IsFailure: true })
        {
            return phone.Error!;
        }

        var password = Passwords.New();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var login = await accounts.CreateAsync(
            new NewLogin(spec.Email, password, spec.Name?.Trim() ?? "", Roles.Merchant) { MerchantId = accountId },
            cancellationToken);
        if (login.IsFailure)
        {
            return login.Error!;
        }

        var moderator = Moderator.Add(accountId, login.Value, spec.Name, phone?.Value.Value, spec.Permissions);
        if (moderator.IsFailure)
        {
            return moderator.Error!;
        }

        db.Moderators.Add(moderator.Value);
        db.OutboxMessages.Add(OutboxContracts.Write(new AccountEmailMessage(login.Value, AccountEmailKind.LoginCreated)));
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return password;
    }

    public async Task<Result> ChangePermissionsAsync(
        long id,
        MerchantPermissions permissions,
        CancellationToken cancellationToken = default)
    {
        var moderator = await FindAsync(id, cancellationToken);
        if (moderator.IsFailure)
        {
            return moderator.Error!;
        }

        var changed = moderator.Value.ChangePermissions(permissions);
        if (changed.IsFailure)
        {
            return changed.Error!;
        }

        await db.SaveChangesAsync(cancellationToken);

        return Result.Success();
    }

    /// <summary>Stops a moderator: their login is shut out and their work in the account ends at once.</summary>
    public async Task<Result> StopAsync(long id, CancellationToken cancellationToken = default)
    {
        var moderator = await FindAsync(id, cancellationToken);
        if (moderator.IsFailure)
        {
            return moderator.Error!;
        }

        moderator.Value.Stop();
        await db.SaveChangesAsync(cancellationToken);
        await accounts.SetLoginEnabledAsync(moderator.Value.UserId, enabled: false, cancellationToken);

        return Result.Success();
    }

    public async Task<Result> LetBackInAsync(long id, CancellationToken cancellationToken = default)
    {
        var moderator = await FindAsync(id, cancellationToken);
        if (moderator.IsFailure)
        {
            return moderator.Error!;
        }

        moderator.Value.LetBackIn();
        await db.SaveChangesAsync(cancellationToken);
        await accounts.SetLoginEnabledAsync(moderator.Value.UserId, enabled: true, cancellationToken);

        return Result.Success();
    }

    /// <summary>A new first password for someone who has lost theirs; shown once, like the one they were added with.</summary>
    public async Task<Result<string>> ResetPasswordAsync(long id, CancellationToken cancellationToken = default)
    {
        var moderator = await FindAsync(id, cancellationToken);
        if (moderator.IsFailure)
        {
            return moderator.Error!;
        }

        var password = Passwords.New();
        var reset = await accounts.ResetPasswordAsync(moderator.Value.UserId, password, cancellationToken);

        return reset.IsFailure ? reset.Error! : password;
    }

    private async Task<Result<Moderator>> FindAsync(long id, CancellationToken cancellationToken)
    {
        var rights = await access.CurrentAsync(cancellationToken);
        if (!rights.MayManageModerators)
        {
            return NotTheOwner;
        }

        if (Account() is not { } accountId)
        {
            return NotFound;
        }

        var moderator = await db.Moderators.SingleOrDefaultAsync(
            m => m.Id == id && m.AccountId == accountId,
            cancellationToken);

        return moderator is null ? NotFound : moderator;
    }

    /// <summary>
    /// The account of the signed-in login: the main profile its businesses hang off, whichever business it is
    /// working in. Moderators belong to the account, not to one business.
    /// </summary>
    private long? Account()
    {
        return currentUser.AccountId;
    }
}
