namespace Application.Abstractions;

/// <summary>
/// Encrypts the secrets a courier keeps in the database (the mail server's and the payment store's passwords). The
/// key never leaves the app's key ring.
/// </summary>
public interface ISecretBox
{
    string Seal(string secret);

    /// <summary>The secret in clear, or null when it can no longer be read (the key it was sealed with is gone).</summary>
    string? Open(string sealedSecret);
}

/// <summary>
/// Where the courier's emails and online payments go: its own mail server and SSLCommerz store when its super admin
/// set them, otherwise the server's configuration (<c>Email:*</c>, <c>Sslcommerz:*</c>). The answer is cached per
/// courier.
/// </summary>
public interface IConnectionSettings
{
    /// <summary>Whether the server's configuration names a mail server, used while the courier sets none.</summary>
    bool ServerHasMail { get; }

    /// <summary>Whether the server's configuration names an SSLCommerz store, used while the courier sets none.</summary>
    bool ServerHasStore { get; }

    /// <summary>
    /// Whether an email to someone reaches them: a mail server is set, or this is Development, where the fake keeps
    /// it on <c>/Dev/Emails</c>. A shop signing up is asked for an emailed code only when it does.
    /// </summary>
    bool EmailsArrive { get; }

    /// <summary>Drops the current courier's cached settings, so a change applies from the next email or payment.</summary>
    void Forget();
}

/// <summary>A mail server to try: where, how to sign in, and how secure the connection is.</summary>
public sealed record MailServerCheck(string Host, int Port, string? UserName, string? Password, string FromAddress, bool UseSsl, bool AllowSelfSigned);

/// <summary>Tries a mail server by connecting and signing in, without sending anything.</summary>
public interface IMailServerCheck
{
    /// <summary>Throws with the server's own words when it will not take the mail.</summary>
    Task CheckAsync(MailServerCheck server, CancellationToken cancellationToken = default);
}
