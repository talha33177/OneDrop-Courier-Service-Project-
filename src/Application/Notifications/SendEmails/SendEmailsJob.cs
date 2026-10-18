using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Application.Abstractions;
using Domain.Notifications;

namespace Application.Notifications.SendEmails;

/// <summary>
/// Sends the tenant's due emails (merchants', the courier's own, recipients' and logins'), oldest first, a batch per
/// run, in a loop of its own so a slow mail server never holds up a recipient's text or a shop's webhook. Each message
/// is saved as sent or failed on its own; a failure waits longer before each retry and is given up after
/// <see cref="OutboxMessage.MaxAttempts"/>.
/// </summary>
public class SendEmailsJob(
    IAppDbContext db,
    MerchantEmails merchants,
    CourierEmails courier,
    RecipientEmails recipients,
    AccountEmails logins,
    TimeProvider time,
    ILogger<SendEmailsJob> logger)
    : ITenantJob
{
    public const int BatchSize = 50;

    /// <summary>The outbox message types this job sends; the other loops leave them alone.</summary>
    public static readonly string[] Types =
        [nameof(MerchantEmailMessage), nameof(CourierEmailMessage), nameof(RecipientEmailMessage), nameof(AccountEmailMessage)];

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow().UtcDateTime;
        var due = await db.OutboxMessages
            .Where(m => m.Status == OutboxStatus.Pending && (m.NextAttemptOn == null || m.NextAttemptOn <= now) &&
                Types.Contains(m.Type))
            .OrderBy(m => m.Id)
            .Take(BatchSize)
            .ToListAsync(cancellationToken);

        foreach (var message in due)
        {
            try
            {
                await (message.Type switch
                {
                    nameof(MerchantEmailMessage) => merchants.SendAsync(message, cancellationToken),
                    nameof(CourierEmailMessage) => courier.SendAsync(message, cancellationToken),
                    nameof(RecipientEmailMessage) => recipients.SendAsync(message, cancellationToken),
                    _ => logins.SendAsync(message, cancellationToken)
                });
                message.MarkSent(now);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                message.MarkFailed(exception.Message, now);
                logger.LogWarning(
                    exception,
                    "Email {Id} ({Type}) failed, attempt {Attempts}",
                    message.Id,
                    message.Type,
                    message.Attempts);
            }

            await db.SaveChangesAsync(cancellationToken);
        }
    }
}
