using Application.Abstractions;
using Application.Notifications;
using Domain.Common;

namespace Application.Platform.Accounts;

/// <summary>
/// "Forgot your password?": an email with a link to choose a new one, sent through the outbox like every email. The
/// answer is the same whether or not the address has a login here, so the page cannot be used to find out who does.
/// </summary>
public class PasswordResetHandler(IAppDbContext db, IUserAccounts accounts)
{
    public async Task RequestAsync(string? email, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(email) || email.Trim().Length > 256)
        {
            return;
        }

        if (await accounts.FindByEmailAsync(email, cancellationToken) is { } login)
        {
            db.OutboxMessages.Add(OutboxContracts.Write(new AccountEmailMessage(login.UserId, AccountEmailKind.PasswordReset)));
            await db.SaveChangesAsync(cancellationToken);
        }
    }

    public Task<Result> ResetAsync(long userId, string? token, string? password, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(token))
        {
            return Task.FromResult<Result>(Error.Validation("login.reset", "This link is not complete. Open it from the email again."));
        }

        return accounts.ResetPasswordWithTokenAsync(userId, token, password ?? "", cancellationToken);
    }

    /// <summary>Emails a login made by someone else where to sign in, with a link to choose a password.</summary>
    public async Task WelcomeAsync(long userId, CancellationToken cancellationToken = default)
    {
        db.OutboxMessages.Add(OutboxContracts.Write(new AccountEmailMessage(userId, AccountEmailKind.LoginCreated)));
        await db.SaveChangesAsync(cancellationToken);
    }
}
