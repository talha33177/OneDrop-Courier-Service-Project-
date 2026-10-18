using System.Collections.Concurrent;
using System.Net;
using System.Net.Mail;
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
/// Sends through an ordinary SMTP server with STARTTLS (Gmail, and anything else that speaks it). One client per
/// message, because <see cref="SmtpClient"/> is not safe to share between sends.
/// </summary>
public class SmtpEmailSender(EmailOptions options, EmailLog log, TimeProvider time, ILogger<SmtpEmailSender> logger)
    : IEmailSender
{
    public async Task SendAsync(EmailMessage message, CancellationToken cancellationToken = default)
    {
        using var mail = new MailMessage
        {
            From = new MailAddress(options.From!, options.FromName ?? options.From!),
            Subject = message.Subject,
            Body = message.Text,
            IsBodyHtml = false
        };
        mail.To.Add(message.ToName is null ? new MailAddress(message.To) : new MailAddress(message.To, message.ToName));
        mail.AlternateViews.Add(AlternateView.CreateAlternateViewFromString(message.Html, null, "text/html"));

        using var client = new SmtpClient(options.Host, options.Port)
        {
            EnableSsl = true,
            Credentials = new NetworkCredential(options.User ?? options.From, options.Password)
        };
        await client.SendMailAsync(mail, cancellationToken);
        log.Add(new SentEmail(time.GetUtcNow().UtcDateTime, message.To, message.Subject, message.Text, message.Html));
        logger.LogInformation("Email sent to {To}: {Subject}", message.To, message.Subject);
    }
}
