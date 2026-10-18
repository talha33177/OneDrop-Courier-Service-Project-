using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Application.Abstractions;
using Application.Common;
using Domain.Notifications;
using Domain.Payments;

namespace Application.Notifications.SendEmails;

/// <summary>
/// Emails to a recipient who paid a parcel's cash on delivery online, at the address they gave on the payment page:
/// the receipt, and the notice when the money goes back. Never the parcel's address or anything the code alone
/// would not show.
/// </summary>
public class RecipientEmails(IAppDbContext db, ITenantContext tenantContext, IEmailSender email, ITrackingLinks links)
{
    public async Task SendAsync(OutboxMessage message, CancellationToken cancellationToken = default)
    {
        var tenant = tenantContext.Tenant ?? throw new InvalidOperationException("Emails need a tenant.");
        var payload = JsonSerializer.Deserialize<RecipientEmailMessage>(message.Payload)
            ?? throw new InvalidOperationException($"Outbox message {message.Id} has no payload.");

        var refund = payload.Kind == RecipientEmailKind.Refunded;
        var found = await (
            from payment in db.OnlinePayments.IgnoreQueryFilters([QueryFilters.Merchant])
            join parcel in db.Parcels.IgnoreQueryFilters([QueryFilters.Merchant]) on payment.ParcelId equals parcel.Id
            join merchant in db.Merchants.IgnoreQueryFilters([QueryFilters.Merchant]) on parcel.MerchantId equals merchant.Id
            where payment.Id == payload.OnlinePaymentId &&
                payment.Status == (refund ? OnlinePaymentStatus.Refunded : OnlinePaymentStatus.Paid)
            select new { payment.Number, payment.Amount, payment.PaidAmount, payment.Currency, payment.Method, payment.PayerEmail, parcel.TrackingCode, parcel.RecipientName, Shop = merchant.Name })
            .AsNoTracking()
            .SingleOrDefaultAsync(cancellationToken);
        if (found?.PayerEmail is null)
        {
            return;
        }

        var amount = EmailLayout.Money(found.PaidAmount ?? found.Amount, found.Currency);
        var method = OnlinePayment.MethodName(found.Method);
        EmailRow[] rows =
        [
            new EmailRow("Parcel", found.TrackingCode),
            new EmailRow("From", found.Shop),
            new EmailRow("Payment", found.Number),
            .. method is null ? Array.Empty<EmailRow>() : [new EmailRow("Paid by", method)],
            new EmailRow(refund ? "Refunded" : "Paid", amount, Strong: true)
        ];
        var content = refund
            ? new EmailContent
            {
                Subject = $"Refund for parcel {found.TrackingCode}: {amount}",
                Preview = $"{amount} is going back to the account you paid from.",
                Label = "Refunded",
                Title = "We refunded your payment",
                Greeting = $"Hello {found.RecipientName},",
                Paragraphs = [$"Your online payment for parcel {found.TrackingCode} from {found.Shop} was refunded. It goes back to the account you paid from; banks and wallets can take a few days to show it."],
                Rows = rows,
                Button = ("Track the parcel", links.Track(found.TrackingCode))
            }
            : new EmailContent
            {
                Subject = $"Receipt: you paid {amount} for parcel {found.TrackingCode}",
                Preview = "Nothing to pay at the door: just take your parcel.",
                Label = "Paid online",
                Tone = EmailTone.Good,
                Title = "Thank you, your parcel is paid",
                Greeting = $"Hello {found.RecipientName},",
                Paragraphs = [$"We received your payment for the parcel from {found.Shop}. There is nothing to pay at the door: just take your parcel from the rider. Keep this email as your receipt."],
                Highlight = (amount, $"paid for parcel {found.TrackingCode}"),
                Rows = rows,
                Button = ("Track your parcel", links.Track(found.TrackingCode)),
                Small = "If the parcel is cancelled or comes back to the shop, we refund this payment to you."
            };

        var written = EmailLayout.Render(content, new EmailBrand(tenant.Name, tenant.SupportPhone, links.Page("")));
        await email.SendAsync(written with { To = found.PayerEmail, ToName = found.RecipientName }, cancellationToken);
    }
}
