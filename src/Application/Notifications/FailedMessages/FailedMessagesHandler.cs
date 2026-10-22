using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Application.Abstractions;
using Application.Common;
using Application.Notifications.SendOutbox;
using Domain.Common;
using Domain.Notifications;
using Domain.Parcels;

namespace Application.Notifications.FailedMessages;

/// <summary>How a message goes out.</summary>
public enum MessageChannel
{
    Text,
    Webhook,
    Email
}

/// <summary>
/// One text, webhook or email that has not gone out: what it was about (a parcel and its status, or what the email
/// says), who it was for (the recipient's phone, the merchant, a login's address), how often it was tried and the last
/// error. <see cref="Created"/> is when it happened, in the courier's time zone. <see cref="GivenUp"/> once every
/// attempt has failed; otherwise it is still being retried.
/// </summary>
public sealed record FailedMessage(
    long Id,
    MessageChannel Channel,
    string About,
    string To,
    DateTime Created,
    int Attempts,
    bool GivenUp,
    string? LastError);

/// <summary>
/// The courier's texts and webhooks that failed: given up after every attempt, or still being retried after a failure,
/// newest first. A given-up message can be sent again once the cause is fixed.
/// </summary>
public class FailedMessagesHandler(IAppDbContext db, ITenantContext tenantContext, IUserAccounts accounts)
{
    public const int Shown = 100;

    public static readonly Error NotFound = Error.NotFound("outbox.notFound", "That message was not found.");

    public async Task<IReadOnlyList<FailedMessage>> ListAsync(CancellationToken cancellationToken = default)
    {
        var tenant = tenantContext.Require();
        var messages = await db.OutboxMessages
            .Where(m => m.Status == OutboxStatus.Failed || (m.Status == OutboxStatus.Pending && m.Attempts > 0))
            .OrderByDescending(m => m.Id)
            .Take(Shown)
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        T Read<T>(OutboxMessage message) => JsonSerializer.Deserialize<T>(message.Payload)!;

        var parcelIds = messages
            .Where(m => m.Type is nameof(RecipientTextMessage) or nameof(ParcelStatusChangedMessage))
            .Select(m => Read<Ids>(m).ParcelId)
            .Distinct()
            .ToList();
        var parcels = await (
            from parcel in db.Parcels
            join merchant in db.Merchants on parcel.MerchantId equals merchant.Id
            where parcelIds.Contains(parcel.Id)
            select new { parcel.Id, parcel.TrackingCode, parcel.RecipientPhone, Merchant = merchant.Name })
            .ToDictionaryAsync(p => p.Id, cancellationToken);
        var merchantIds = messages.Where(m => m.Type == nameof(MerchantEmailMessage)).Select(m => Read<MerchantEmailMessage>(m).MerchantId).Distinct().ToList();
        var merchants = await db.Merchants.Where(m => merchantIds.Contains(m.Id)).ToDictionaryAsync(m => m.Id, m => m.Name, cancellationToken);
        var paymentIds = messages.Where(m => m.Type == nameof(RecipientEmailMessage)).Select(m => Read<RecipientEmailMessage>(m).OnlinePaymentId).Distinct().ToList();
        var payers = await db.OnlinePayments.Where(p => paymentIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id, p => p.PayerEmail, cancellationToken);
        var logins = await accounts.EmailsAsync(
            [.. messages.Where(m => m.Type == nameof(AccountEmailMessage)).Select(m => Read<AccountEmailMessage>(m).UserId).Distinct()],
            cancellationToken);

        return [.. messages.Select(Describe)];

        FailedMessage Describe(OutboxMessage message)
        {
            var (channel, about, to) = message.Type switch
            {
                nameof(MerchantEmailMessage) => Read<MerchantEmailMessage>(message) is var e
                    ? (MessageChannel.Email, Says(e.Kind), merchants.GetValueOrDefault(e.MerchantId) ?? "")
                    : default,
                nameof(CourierEmailMessage) => (MessageChannel.Email, "A new sign-up to approve", "The courier's super admins"),
                nameof(RecipientEmailMessage) => Read<RecipientEmailMessage>(message) is var r
                    ? (MessageChannel.Email,
                        r.Kind == RecipientEmailKind.Refunded ? "Refund of an online payment" : "Receipt for an online payment",
                        payers.GetValueOrDefault(r.OnlinePaymentId) ?? "The recipient who paid")
                    : default,
                nameof(AccountEmailMessage) => Read<AccountEmailMessage>(message) is var a
                    ? (MessageChannel.Email,
                        a.Kind == AccountEmailKind.PasswordReset ? "A link to choose a new password" : "A new login and where to sign in",
                        logins.GetValueOrDefault(a.UserId) ?? "")
                    : default,
                _ => Read<Ids>(message) is var ids && parcels.TryGetValue(ids.ParcelId, out var p)
                    ? RecipientTexts.Types.Contains(message.Type)
                        ? (MessageChannel.Text, $"Parcel {p.TrackingCode}: {ids.Status.DisplayName().ToLowerInvariant()}", PhoneNumber.Parse(p.RecipientPhone).Value.Local)
                        : (MessageChannel.Webhook, $"Parcel {p.TrackingCode}: {ids.Status.DisplayName().ToLowerInvariant()}", p.Merchant)
                    : (RecipientTexts.Types.Contains(message.Type) ? MessageChannel.Text : MessageChannel.Webhook, "A parcel", "")
            };

            return new FailedMessage(
                message.Id,
                channel,
                about,
                to,
                tenant.Local(message.Created),
                message.Attempts,
                message.Status == OutboxStatus.Failed,
                message.LastError);
        }
    }

    /// <summary>Puts a given-up message back in the queue; the senders pick it up within seconds.</summary>
    public async Task<Result> SendAgainAsync(long id, CancellationToken cancellationToken = default)
    {
        var message = await db.OutboxMessages.SingleOrDefaultAsync(m => m.Id == id, cancellationToken);
        if (message is null)
        {
            return NotFound;
        }

        var again = message.SendAgain();
        if (again.IsSuccess)
        {
            await db.SaveChangesAsync(cancellationToken);
        }

        return again;
    }

    /// <summary>What a merchant's email was telling it, in words.</summary>
    private static string Says(MerchantEmailKind kind)
    {
        return kind switch
        {
            MerchantEmailKind.PayoutSent => "Its payout was sent",
            MerchantEmailKind.SignedUp => "Welcome after signing up",
            MerchantEmailKind.Approved => "Its account was approved",
            MerchantEmailKind.Suspended => "Its account was suspended",
            MerchantEmailKind.Reactivated => "Its account is active again",
            MerchantEmailKind.PayoutsHeld => "Its payouts are held",
            MerchantEmailKind.PayoutsReleased => "Its payouts are released",
            MerchantEmailKind.PayoutRequestRefused => "Its request to be paid now was refused",
            MerchantEmailKind.ParcelRequestAnswered => "A request about a parcel was answered",
            MerchantEmailKind.ReturnListToConfirm => "Returns handed back, to confirm",
            MerchantEmailKind.PaymentReceived => "Its online payment was received",
            MerchantEmailKind.PaymentRefunded => "Its online payment was refunded",
            MerchantEmailKind.Terminated => "Its account was terminated",
            MerchantEmailKind.Restored => "Its account was brought back",
            _ => "Its balance was adjusted"
        };
    }

    /// <summary>The ids a parcel's text and webhook carry.</summary>
    private sealed record Ids(long ParcelId, ParcelStatus Status);
}
