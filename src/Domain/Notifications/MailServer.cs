using Domain.Common;

namespace Domain.Notifications;

/// <summary>What the super admin types about the courier's mail server, without the password.</summary>
public sealed record MailServerDetails(
    string? Host,
    int Port,
    string? UserName,
    string? FromAddress,
    string? FromName,
    bool IsEnabled = true,
    bool UseSsl = true,
    bool AllowSelfSigned = false);

/// <summary>
/// The SMTP server the courier's emails go out through, kept by its super admin. At most one per courier
/// (<c>UX_MailServer_Tenant</c>); with none, the server's own <c>Email:*</c> settings are used. <see cref="Password"/>
/// is the password as the app encrypted it (this entity never sees it in clear), or empty for a server that takes mail
/// without a login. A server switched off keeps its details and sends nothing.
/// </summary>
public class MailServer : TenantEntity
{
    private MailServer()
    {
    }

    public string Host { get; private set; } = "";

    public int Port { get; private set; }

    /// <summary>The login the server knows; null when it is the from address.</summary>
    public string? UserName { get; private set; }

    public string Password { get; private set; } = "";

    public string FromAddress { get; private set; } = "";

    public string? FromName { get; private set; }

    public bool IsEnabled { get; private set; } = true;

    /// <summary>A secure connection: STARTTLS, or SSL from the start on port 465.</summary>
    public bool UseSsl { get; private set; } = true;

    /// <summary>Accepts a certificate nobody vouches for, as a mail server of the courier's own often has.</summary>
    public bool AllowSelfSigned { get; private set; }

    public bool HasPassword => Password.Length > 0;

    public static Result<MailServer> Create(MailServerDetails details, string sealedPassword)
    {
        var server = new MailServer { Password = sealedPassword };
        var changed = server.Change(details, null);

        return changed.IsSuccess ? server : changed.Error!;
    }

    /// <summary>Changes the details; a null <paramref name="sealedPassword"/> keeps the password it has.</summary>
    public Result Change(MailServerDetails details, string? sealedPassword)
    {
        var host = details.Host.NullIfBlank();
        if (host is null || host.Length > 200 || !host.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-'))
        {
            return Error.Validation("mail.host", "Enter the mail server's address, such as smtp.gmail.com.");
        }

        if (details.Port is < 1 or > 65535)
        {
            return Error.Validation("mail.port", "The port is a number from 1 to 65535; 587 for most servers, 465 for SSL from the start.");
        }

        var from = details.FromAddress.NullIfBlank();
        if (!IsEmail(from))
        {
            return Error.Validation("mail.from", "Enter the address the emails come from, such as hello@yourcourier.com.");
        }

        var user = details.UserName.NullIfBlank();
        if (user is { Length: > 254 })
        {
            return Error.Validation("mail.user", "The login is at most 254 characters.");
        }

        var name = details.FromName.NullIfBlank();
        if (name is { Length: > 200 })
        {
            return Error.Validation("mail.fromName", "The sender's name is at most 200 characters.");
        }

        Host = host;
        Port = details.Port;
        UserName = user;
        FromAddress = from!;
        FromName = name;
        IsEnabled = details.IsEnabled;
        UseSsl = details.UseSsl;
        AllowSelfSigned = details.AllowSelfSigned && details.UseSsl;
        Password = sealedPassword ?? Password;

        return Result.Success();
    }

    /// <summary>Forgets the password: the server is then tried without a login.</summary>
    public void ClearPassword()
    {
        Password = "";
    }

    private static bool IsEmail(string? value)
    {
        var at = value?.IndexOf('@') ?? -1;

        return value is { Length: <= 254 } && at > 0 && at < value.Length - 1 && value.IndexOf('@', at + 1) < 0 && !value.Any(char.IsWhiteSpace);
    }
}
