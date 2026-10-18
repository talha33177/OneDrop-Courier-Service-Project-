using System.Collections.Concurrent;
using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;
using Microsoft.Extensions.Logging;
using Application.Abstractions;

namespace Infrastructure.Email;

/// <summary>
/// The mail server to send through, from configuration (`Email:*`). The password belongs in a git-ignored
/// <c>*.Local.json</c>, never in the repository. With no <see cref="Host"/> the app uses
/// <see cref="FakeEmailSender"/> instead.
/// </summary>
public sealed class EmailOptions
{
    public string? Host { get; set; }

    public int Port { get; set; } = 587;

    public string? User { get; set; }

    public string? Password { get; set; }

    /// <summary>The address the courier writes from. Gmail sends as the account's own address whatever this says.</summary>
    public string? From { get; set; }

    public string? FromName { get; set; }

    /// <summary>A secure connection: STARTTLS, or SSL from the start on port 465.</summary>
    public bool UseSsl { get; set; } = true;

    /// <summary>Accepts a certificate nobody vouches for, for a mail server of the courier's own.</summary>
    public bool AllowSelfSigned { get; set; }

    public bool Configured => !string.IsNullOrWhiteSpace(Host) && !string.IsNullOrWhiteSpace(From);
}

public sealed record SentEmail(DateTime On, string To, string Subject, string Text, string Html);

/// <summary>The emails the fake sender "sent", so a demo can read them without a mailbox.</summary>
public class EmailLog
{
    private const int Keep = 500;
    private readonly ConcurrentQueue<SentEmail> messages = new();

    public IReadOnlyList<SentEmail> Recent => [.. messages.Reverse()];

    public void Add(SentEmail message)
    {
        messages.Enqueue(message);
        while (messages.Count > Keep)
        {
            messages.TryDequeue(out _);
        }
    }
}

/// <summary>Stand-in for the mail server: logs the email and keeps it in <see cref="EmailLog"/> for <c>/Dev/Emails</c>.</summary>
public class FakeEmailSender(EmailLog log, TimeProvider time, ILogger<FakeEmailSender> logger) : IEmailSender
{
    public Task SendAsync(EmailMessage message, CancellationToken cancellationToken = default)
    {
        log.Add(new SentEmail(time.GetUtcNow().UtcDateTime, message.To, message.Subject, message.Text, message.Html));
        logger.LogInformation("Email to {To}: {Subject}", message.To, message.Subject);

        return Task.CompletedTask;
    }
}

/// <summary>
/// Sends through an ordinary SMTP server (Gmail, a company's own mail server, anything that speaks it) with MailKit. One
/// connection per message. A secure connection is STARTTLS, or SSL from the start on port 465; with
/// <see cref="EmailOptions.AllowSelfSigned"/> this one connection accepts a certificate nobody vouches for, and nothing
/// else in the app does.
/// </summary>
public class SmtpEmailSender(EmailOptions options, EmailLog log, TimeProvider time, ILogger<SmtpEmailSender> logger)
    : IEmailSender
{
    public async Task SendAsync(EmailMessage message, CancellationToken cancellationToken = default)
    {
        var mail = new MimeMessage();
        mail.From.Add(new MailboxAddress(options.FromName ?? options.From!, options.From!));
        mail.To.Add(message.ToName is null ? MailboxAddress.Parse(message.To) : new MailboxAddress(message.ToName, message.To));
        mail.Subject = message.Subject;
        mail.Body = new BodyBuilder { TextBody = message.Text, HtmlBody = message.Html }.ToMessageBody();

        using var client = await ConnectAsync(options, cancellationToken);
        try
        {
            await client.SendAsync(mail, cancellationToken);
            await client.DisconnectAsync(true, cancellationToken);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw Slow(options);
        }

        log.Add(new SentEmail(time.GetUtcNow().UtcDateTime, message.To, message.Subject, message.Text, message.Html));
        logger.LogInformation("Email sent to {To}: {Subject}", message.To, message.Subject);
    }

    /// <summary>
    /// Connects and signs in, sending nothing: whether this server will take the courier's mail. Throws with the
    /// server's own words when it will not.
    /// </summary>
    public static async Task CheckAsync(EmailOptions options, CancellationToken cancellationToken = default)
    {
        using var client = await ConnectAsync(options, cancellationToken);
        await client.DisconnectAsync(true, cancellationToken);
    }

    /// <summary>A server that did not answer within the client's time, in words the admin can act on.</summary>
    private static TimeoutException Slow(EmailOptions options)
    {
        return new TimeoutException(
            $"{options.Host}:{options.Port} did not answer in time. Check the host and the port, and that this server may reach it.");
    }

    private static async Task<SmtpClient> ConnectAsync(EmailOptions options, CancellationToken cancellationToken)
    {
        var client = new SmtpClient { Timeout = 20_000 };
        try
        {
            if (options is { UseSsl: true, AllowSelfSigned: true })
            {
                client.ServerCertificateValidationCallback = (_, _, _, _) => true;
            }

            var secure = !options.UseSsl
                ? SecureSocketOptions.None
                : options.Port == 465 ? SecureSocketOptions.SslOnConnect : SecureSocketOptions.StartTls;
            await client.ConnectAsync(options.Host!, options.Port, secure, cancellationToken);
            if (!string.IsNullOrEmpty(options.Password))
            {
                await client.AuthenticateAsync(options.User is { Length: > 0 } user ? user : options.From!, options.Password, cancellationToken);
            }

            return client;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            client.Dispose();

            throw Slow(options);
        }
        catch
        {
            client.Dispose();

            throw;
        }
    }
}
