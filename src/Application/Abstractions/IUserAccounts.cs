using Domain.Common;

namespace Application.Abstractions;

/// <summary>A login to create for the current tenant: a merchant's user (with <see cref="MerchantId"/>), a rider or staff.</summary>
public sealed record NewLogin(string? Email, string? Password, string DisplayName, string Role)
{
    public long? MerchantId { get; init; }
}

/// <summary>A login of the courier's own people (an admin or hub staff), as the admin sees it.</summary>
public sealed record StaffLogin(long UserId, string Name, string Email, string Role, bool Stopped, DateTime Created);

/// <summary>One login as an email needs it: where to write, whom to greet, and whether it may still sign in.</summary>
public sealed record LoginInfo(long UserId, string Email, string Name, bool Stopped);

/// <summary>Creates logins. Identity lives in Infrastructure; the use cases only ask for an account.</summary>
public interface IUserAccounts
{
    /// <summary>The current courier's login with this id, or null.</summary>
    Task<LoginInfo?> FindAsync(long userId, CancellationToken cancellationToken = default);

    /// <summary>The current courier's login with this email that may sign in, or null.</summary>
    Task<LoginInfo?> FindByEmailAsync(string email, CancellationToken cancellationToken = default);

    /// <summary>
    /// A one-time token that lets the login choose a new password, for a link in an email. It stops working once used,
    /// once the password changes another way, or after a day.
    /// </summary>
    Task<string> PasswordTokenAsync(long userId, CancellationToken cancellationToken = default);

    /// <summary>Sets a new password with a token from <see cref="PasswordTokenAsync"/>, or says why not.</summary>
    Task<Result> ResetPasswordWithTokenAsync(long userId, string token, string password, CancellationToken cancellationToken = default);

    /// <summary>The new login's id, or why it was refused (email taken at this courier, weak password).</summary>
    Task<Result<long>> CreateAsync(NewLogin login, CancellationToken cancellationToken = default);

    /// <summary>The emails of the logins with these ids, for showing who can sign in.</summary>
    Task<IReadOnlyDictionary<long, string>> EmailsAsync(IReadOnlyCollection<long> userIds, CancellationToken cancellationToken = default);

    /// <summary>The email of a merchant's first login, or null.</summary>
    Task<string?> MerchantEmailAsync(long merchantId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Lets a login in or shuts it out, for a person stopped by whoever added them. A shut-out login is refused at
    /// sign-in, and a session already open ends at its next security check.
    /// </summary>
    Task SetLoginEnabledAsync(long userId, bool enabled, CancellationToken cancellationToken = default);

    /// <summary>Gives a login a new password, for an owner who has to hand one out again.</summary>
    Task<Result> ResetPasswordAsync(long userId, string password, CancellationToken cancellationToken = default);

    /// <summary>The current courier's logins in one of <paramref name="roles"/>, each with the first of them it holds.</summary>
    Task<IReadOnlyList<StaffLogin>> InRolesAsync(IReadOnlyList<string> roles, CancellationToken cancellationToken = default);

    /// <summary>
    /// Renames a login and moves it from role <paramref name="from"/> to <paramref name="to"/>. An open session picks
    /// the new role up at its next security check.
    /// </summary>
    Task<Result> ChangeAsync(long userId, string name, string from, string to, CancellationToken cancellationToken = default);
}
