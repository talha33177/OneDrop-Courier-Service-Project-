using Microsoft.EntityFrameworkCore;
using Application.Abstractions;
using Application.Notifications.SendEmails;
using Domain.Common;
using Domain.Notifications;
using Domain.Payments;

namespace Application.Platform.Connections;

/// <summary>
/// The courier's saved mail server as the page shows it. <paramref name="PasswordHint"/> is the password with its
/// middle hidden, so the admin can tell which one is saved; null when none is (a server that takes mail without a login).
/// </summary>
public sealed record MailServerView(
    string Host,
    int Port,
    string? UserName,
    string FromAddress,
    string? FromName,
    bool IsEnabled,
    bool UseSsl,
    bool AllowSelfSigned,
    bool HasPassword,
    string? PasswordHint,
    bool PasswordReadable);

public sealed record GatewayStoreView(string StoreId, bool IsLive, bool PasswordReadable);

/// <summary>The courier's own mail server and store (null where it set none) and whether the server has its own.</summary>
public sealed record ConnectionsView(MailServerView? Mail, GatewayStoreView? Store, bool ServerHasMail, bool ServerHasStore);

/// <summary>
/// The mail server the courier's emails go out through and the SSLCommerz store it takes online payments with, kept
/// by its super admin. Passwords are sealed before they are stored and never shown back, except for their first and last
/// two characters when they are long enough to hide the rest: an empty password box keeps the one saved. Removing
/// either goes back to the server's own settings.
/// </summary>
public class ConnectionsHandler(
    IAppDbContext db,
    ISecretBox secrets,
    IConnectionSettings settings,
    IEmailSender email,
    IMailServerCheck mailCheck,
    ITrackingLinks links)
{
    public const int MaxPasswordLength = 200;

    public async Task<ConnectionsView> GetAsync(CancellationToken cancellationToken = default)
    {
        var mail = await db.MailServers.AsNoTracking().SingleOrDefaultAsync(cancellationToken);
        var store = await db.GatewayStores.AsNoTracking().SingleOrDefaultAsync(cancellationToken);
        var clear = mail is { HasPassword: true } ? secrets.Open(mail.Password) : null;

        return new ConnectionsView(
            mail is null
                ? null
                : new MailServerView(
                    mail.Host,
                    mail.Port,
                    mail.UserName,
                    mail.FromAddress,
                    mail.FromName,
                    mail.IsEnabled,
                    mail.UseSsl,
                    mail.AllowSelfSigned,
                    mail.HasPassword,
                    clear is null ? null : Hint(clear),
                    !mail.HasPassword || clear is not null),
            store is null ? null : new GatewayStoreView(store.StoreId, store.IsLive, secrets.Open(store.StorePassword) is not null),
            settings.ServerHasMail,
            settings.ServerHasStore);
    }

    /// <summary>The first and last two characters of a long password with the middle hidden; a short one is hidden whole.</summary>
    public static string Hint(string password)
    {
        return password.Length >= 12 ? $"{password[..2]}••••••{password[^2..]}" : "••••••••";
    }

    public async Task<Result> SaveMailAsync(MailServerDetails details, string? password, CancellationToken cancellationToken = default)
    {
        var sealedPassword = Seal(password, "mail.password");
        if (sealedPassword.IsFailure)
        {
            return sealedPassword.Error!;
        }

        var server = await db.MailServers.SingleOrDefaultAsync(cancellationToken);
        if (server is null)
        {
            if (sealedPassword.Value is null)
            {
                return Error.Validation("mail.password", "Enter the mail server's password (for Gmail, an app password).");
            }

            var created = MailServer.Create(details, sealedPassword.Value);
            if (created.IsFailure)
            {
                return created.Error!;
            }

            db.MailServers.Add(created.Value);
        }
        else
        {
            var changed = server.Change(details, sealedPassword.Value);
            if (changed.IsFailure)
            {
                return changed;
            }
        }

        await db.SaveChangesAsync(cancellationToken);
        settings.Forget();

        return Result.Success();
    }

    /// <summary>Forgets the saved password: the server is then tried without a login.</summary>
    public async Task<Result> RemoveMailPasswordAsync(CancellationToken cancellationToken = default)
    {
        var server = await db.MailServers.SingleOrDefaultAsync(cancellationToken);
        if (server is null)
        {
            return Error.NotFound("mail.none", "No mail server is saved.");
        }

        server.ClearPassword();
        await db.SaveChangesAsync(cancellationToken);
        settings.Forget();

        return Result.Success();
    }

    public async Task RemoveMailAsync(CancellationToken cancellationToken = default)
    {
        await db.MailServers.ExecuteDeleteAsync(cancellationToken);
        settings.Forget();
    }

    /// <summary>
    /// Connects to the server as typed and signs in, sending nothing and saving nothing. An empty password box tries
    /// the password saved, if there is one.
    /// </summary>
    public async Task<Result<string>> CheckMailAsync(MailServerDetails details, string? password, CancellationToken cancellationToken = default)
    {
        var host = details.Host?.Trim();
        if (string.IsNullOrEmpty(host) || details.Port is < 1 or > 65535 || string.IsNullOrWhiteSpace(details.FromAddress))
        {
            return Error.Validation("mail.check", "Enter the server, the port and the sender email first.");
        }

        var saved = string.IsNullOrEmpty(password)
            ? await db.MailServers.AsNoTracking().Select(s => s.Password).SingleOrDefaultAsync(cancellationToken)
            : null;
        var word = !string.IsNullOrEmpty(password) ? password : saved is { Length: > 0 } ? secrets.Open(saved) : null;
        try
        {
            await mailCheck.CheckAsync(
                new MailServerCheck(host, details.Port, details.UserName?.Trim(), word, details.FromAddress.Trim(), details.UseSsl, details.AllowSelfSigned),
                cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return Error.Conflict("mail.check", $"The server did not accept the connection: {exception.GetBaseException().Message}");
        }

        return $"Connected to {host}:{details.Port}{(string.IsNullOrEmpty(word) ? " (no login tried)" : " and signed in")}. Nothing was sent.";
    }

    public async Task<Result> SaveStoreAsync(string? storeId, bool isLive, string? password, CancellationToken cancellationToken = default)
    {
        var sealedPassword = Seal(password, "store.password");
        if (sealedPassword.IsFailure)
        {
            return sealedPassword.Error!;
        }

        var store = await db.GatewayStores.SingleOrDefaultAsync(cancellationToken);
        if (store is null)
        {
            if (sealedPassword.Value is null)
            {
                return Error.Validation("store.password", "Enter the store password SSLCommerz gave you.");
            }

            var created = GatewayStore.Create(storeId, isLive, sealedPassword.Value);
            if (created.IsFailure)
            {
                return created.Error!;
            }

            db.GatewayStores.Add(created.Value);
        }
        else
        {
            var changed = store.Change(storeId, isLive, sealedPassword.Value);
            if (changed.IsFailure)
            {
                return changed;
            }
        }

        await db.SaveChangesAsync(cancellationToken);
        settings.Forget();

        return Result.Success();
    }

    public async Task RemoveStoreAsync(CancellationToken cancellationToken = default)
    {
        await db.GatewayStores.ExecuteDeleteAsync(cancellationToken);
        settings.Forget();
    }

    /// <summary>
    /// Sends a short email through the mail server in use to <paramref name="to"/>, and says what the server answered
    /// when it refused.
    /// </summary>
    public async Task<Result> SendTestAsync(string to, string courierName, string hotline, CancellationToken cancellationToken = default)
    {
        try
        {
            var content = new EmailContent
            {
                Subject = $"Test email from {courierName}",
                Preview = "Your mail server works.",
                Label = "Test email",
                Tone = EmailTone.Good,
                Title = "Your mail server works",
                Paragraphs =
                [
                    "This test email reached you, so the mail server saved on Email and payments works.",
                    "Merchants, recipients who pay online and your own people now get their emails from it: payouts, approvals, answers to requests, returns to confirm, receipts and password links."
                ],
                Button = ("Open Email and payments", links.Page("Admin/Connections"))
            };
            var written = EmailLayout.Render(content, new EmailBrand(courierName, hotline, links.Page("")));
            await email.SendAsync(written with { To = to }, cancellationToken);

            return Result.Success();
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return Error.Conflict("mail.test", $"The mail server refused it: {exception.GetBaseException().Message}");
        }
    }

    /// <summary>The password sealed for storing, or null when the box was left empty to keep the saved one.</summary>
    private Result<string?> Seal(string? password, string code)
    {
        if (string.IsNullOrEmpty(password))
        {
            return Result.Success<string?>(null);
        }

        if (password.Length > MaxPasswordLength)
        {
            return Error.Validation(code, $"The password is at most {MaxPasswordLength} characters.");
        }

        return Result.Success<string?>(secrets.Seal(password));
    }
}
