using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Application.Abstractions;
using Application.Common;
using Domain.Delivery;
using Domain.Merchants;
using Domain.Notifications;
using Domain.Parcels;
using Domain.Payments;

namespace Application.Notifications.SendEmails;

/// <summary>
/// Writes and sends a merchant's email for an outbox message, from the data as it is when sent, like the recipients'
/// texts: an email whose news is out of date by then (a hold already released, a list already confirmed) is dropped.
/// It goes to the business's contact email, or else to its account's first login; a merchant with neither is skipped
/// rather than retried for ever.
/// </summary>
public class MerchantEmails(
    IAppDbContext db,
    ITenantContext tenantContext,
    IEmailSender email,
    IUserAccounts accounts,
    ITrackingLinks links)
{
    public async Task SendAsync(OutboxMessage message, CancellationToken cancellationToken = default)
    {
        var tenant = tenantContext.Tenant ?? throw new InvalidOperationException("Emails need a tenant.");
        var payload = JsonSerializer.Deserialize<MerchantEmailMessage>(message.Payload)
            ?? throw new InvalidOperationException($"Outbox message {message.Id} has no payload.");

        var merchant = await db.Merchants
            .IgnoreQueryFilters([QueryFilters.Merchant])
            .AsNoTracking()
            .SingleOrDefaultAsync(m => m.Id == payload.MerchantId, cancellationToken)
            ?? throw new InvalidOperationException($"Merchant {payload.MerchantId} of outbox message {message.Id} is gone.");

        var to = merchant.ContactEmail ?? await accounts.MerchantEmailAsync(merchant.AccountId, cancellationToken);
        if (to is null)
        {
            // Nowhere to write to: the merchant gave no email and its account has no login left
            return;
        }

        var hello = $"Hello {merchant.Name},";
        var content = payload.Kind switch
        {
            MerchantEmailKind.PayoutSent => await PayoutSentAsync(payload.SubjectId, hello, tenant, cancellationToken),
            MerchantEmailKind.SignedUp => SignedUp(merchant, hello, tenant),
            MerchantEmailKind.Approved => merchant.Status == MerchantStatus.Active ? Approved(hello, tenant) : null,
            MerchantEmailKind.Suspended => merchant.Status == MerchantStatus.Suspended ? Suspended(hello, tenant) : null,
            MerchantEmailKind.Reactivated => merchant.Status == MerchantStatus.Active ? Reactivated(hello, tenant) : null,
            MerchantEmailKind.PayoutsHeld => merchant.PayoutHold is { } hold ? PayoutsHeld(hello, hold, tenant) : null,
            MerchantEmailKind.PayoutsReleased => merchant.PayoutHold is null ? PayoutsReleased(hello) : null,
            MerchantEmailKind.PayoutRequestRefused => await PayoutRequestRefusedAsync(payload.SubjectId, hello, tenant, cancellationToken),
            MerchantEmailKind.ParcelRequestAnswered => await ParcelRequestAnsweredAsync(payload.SubjectId, hello, tenant, cancellationToken),
            MerchantEmailKind.ReturnListToConfirm => await ReturnListAsync(payload.SubjectId, hello, tenant, cancellationToken),
            MerchantEmailKind.PaymentReceived => await PaymentAsync(payload.SubjectId, hello, tenant, refunded: false, cancellationToken),
            MerchantEmailKind.PaymentRefunded => await PaymentAsync(payload.SubjectId, hello, tenant, refunded: true, cancellationToken),
            MerchantEmailKind.Adjustment => await AdjustmentAsync(payload.SubjectId, hello, tenant, cancellationToken),
            _ => null
        };
        if (content is null)
        {
            return;
        }

        var written = EmailLayout.Render(content, new EmailBrand(tenant.Name, tenant.SupportPhone, links.Page("")));
        await email.SendAsync(written with { To = to, ToName = merchant.Name }, cancellationToken);
    }

    private EmailContent SignedUp(Merchant merchant, string hello, TenantInfo tenant)
    {
        return new EmailContent
        {
            Subject = $"Welcome to {tenant.Name}, {merchant.Name}",
            Preview = "Your account is waiting for approval. Here is what to do meanwhile.",
            Label = "Welcome",
            Title = $"Welcome to {tenant.Name}!",
            Greeting = hello,
            Paragraphs =
            [
                $"Thank you for signing up. We check every new shop before it can book parcels, and we will email you the moment {merchant.Name} is approved.",
                "While you wait, you can already set your shop up: add the bKash, Nagad or bank account your cash goes to, and check your pickup point, so the first parcel goes out without a hitch."
            ],
            Rows =
            [
                new EmailRow("Shop", merchant.Name),
                new EmailRow("Owner", merchant.OwnerName),
                new EmailRow("Phone", merchant.ContactPhone)
            ],
            Button = ("Open your merchant panel", links.Page("Merchant")),
            Small = "Did not sign up? Call our hotline and we will remove the account."
        };
    }

    private EmailContent Approved(string hello, TenantInfo tenant)
    {
        return new EmailContent
        {
            Subject = $"You're approved: start sending parcels with {tenant.Name}",
            Preview = "Your account is active. Book your first parcel now.",
            Label = "Account approved",
            Tone = EmailTone.Good,
            Title = "Your account is approved",
            Greeting = hello,
            Paragraphs =
            [
                "Good news: your account is active, and you can book parcels from now on.",
                "Book a parcel by form, upload many at once from a spreadsheet, or connect your website through the API. Each parcel booked is picked up from your pickup point, and the cash we collect is paid to you the next day, less our charges."
            ],
            Button = ("Book your first parcel", links.Page("Merchant/NewParcel"))
        };
    }

    private EmailContent Suspended(string hello, TenantInfo tenant)
    {
        return new EmailContent
        {
            Subject = $"Your {tenant.Name} account is suspended",
            Preview = "You cannot book new parcels for now. Call us to sort it out.",
            Label = "Account suspended",
            Tone = EmailTone.Bad,
            Title = "Your account is suspended",
            Greeting = hello,
            Paragraphs =
            [
                "We have suspended your account, so no new parcels can be booked for now. Parcels already on their way are still delivered, and the cash collected for you is still yours.",
                $"Please call our hotline on {tenant.SupportPhone} so we can sort it out together."
            ],
            Button = ("Open your merchant panel", links.Page("Merchant"))
        };
    }

    private EmailContent Reactivated(string hello, TenantInfo tenant)
    {
        return new EmailContent
        {
            Subject = $"Welcome back: your {tenant.Name} account is active again",
            Preview = "You can book parcels again.",
            Label = "Account active",
            Tone = EmailTone.Good,
            Title = "Your account is active again",
            Greeting = hello,
            Paragraphs = ["Your account is no longer suspended. You can book parcels again from now on."],
            Button = ("Book a parcel", links.Page("Merchant/NewParcel"))
        };
    }

    private EmailContent PayoutsHeld(string hello, string reason, TenantInfo tenant)
    {
        return new EmailContent
        {
            Subject = "Your payouts are on hold",
            Preview = reason,
            Label = "Payouts on hold",
            Tone = EmailTone.Warning,
            Title = "We are holding your payouts for now",
            Greeting = hello,
            Paragraphs =
            [
                "Your parcels keep moving and the cash we collect is still recorded for you, but we are not sending it to your account until the hold is lifted.",
                $"If anything is unclear, call our hotline on {tenant.SupportPhone}."
            ],
            Note = ("Why", reason),
            Button = ("See your payments", links.Page("Merchant/Payments"))
        };
    }

    private EmailContent PayoutsReleased(string hello)
    {
        return new EmailContent
        {
            Subject = "Your payouts are back on",
            Preview = "What you are owed goes out with the next payout run.",
            Label = "Payouts resumed",
            Tone = EmailTone.Good,
            Title = "Your payouts are back on",
            Greeting = hello,
            Paragraphs = ["We have lifted the hold on your payouts. Everything you are owed goes out with the next payout run, as usual."],
            Button = ("See your payments", links.Page("Merchant/Payments"))
        };
    }

    private async Task<EmailContent?> PayoutSentAsync(long payoutId, string hello, TenantInfo tenant, CancellationToken cancellationToken)
    {
        var payout = await db.Payouts
            .IgnoreQueryFilters([QueryFilters.Merchant])
            .Where(p => p.Id == payoutId && p.Status == PayoutStatus.Paid)
            .Select(p => new { p.Number, p.Amount, p.CodTotal, p.ChargesTotal, p.AdjustmentsTotal, p.Method, p.Account, p.UpToDate })
            .AsNoTracking()
            .SingleOrDefaultAsync(cancellationToken);
        if (payout is null)
        {
            // The payout is gone or was never confirmed; there is nothing to tell the merchant
            return null;
        }

        var currency = tenant.CurrencyCode;
        var account = payout.Account.Length > 4 ? $"…{payout.Account[^4..]}" : payout.Account;

        return new EmailContent
        {
            Subject = $"{EmailLayout.Money(payout.Amount, currency)} is on its way to you ({payout.Number})",
            Preview = $"Sent to your {payout.Method} account {account}.",
            Label = "Payout sent",
            Tone = EmailTone.Good,
            Title = "Your money is on its way",
            Greeting = hello,
            Paragraphs = [$"We have sent your payout to your {payout.Method} account {account}. Here is how it adds up."],
            Highlight = (EmailLayout.Money(payout.Amount, currency), $"Sent to {payout.Method} {account}"),
            Rows =
            [
                new EmailRow("Cash collected for you", EmailLayout.Money(payout.CodTotal, currency)),
                new EmailRow("Our charges", $"−{EmailLayout.Money(payout.ChargesTotal, currency)}"),
                .. payout.AdjustmentsTotal == 0
                    ? Array.Empty<EmailRow>()
                    : [new EmailRow("Adjustments", EmailLayout.Signed(payout.AdjustmentsTotal, currency))],
                new EmailRow("Sent to you", EmailLayout.Money(payout.Amount, currency), Strong: true),
                new EmailRow("For parcels up to", payout.UpToDate.ToString("d MMMM yyyy", CultureInfo.InvariantCulture)),
                new EmailRow("Invoice", payout.Number)
            ],
            Button = ($"See invoice {payout.Number}", links.Invoice(payout.Number))
        };
    }

    private async Task<EmailContent?> PayoutRequestRefusedAsync(long requestId, string hello, TenantInfo tenant, CancellationToken cancellationToken)
    {
        var request = await db.PayoutRequests
            .IgnoreQueryFilters([QueryFilters.Merchant])
            .AsNoTracking()
            .SingleOrDefaultAsync(r => r.Id == requestId && r.Status == PayoutRequestStatus.Refused, cancellationToken);
        if (request is null)
        {
            return null;
        }

        return new EmailContent
        {
            Subject = "Your request to be paid now was not approved",
            Preview = request.Answer ?? "Your balance goes out with the daily payouts.",
            Label = "Request answered",
            Tone = EmailTone.Warning,
            Title = "We could not pay you early this time",
            Greeting = hello,
            Paragraphs =
            [
                $"You asked to be paid {EmailLayout.Money(request.AmountAsked, tenant.CurrencyCode)} now. We are not able to send it early, but nothing is lost: your balance goes out with the daily payouts, the morning after each day."
            ],
            Note = request.Answer is null ? null : ("Our reason", request.Answer),
            Button = ("See your payments", links.Page("Merchant/Payments"))
        };
    }

    private async Task<EmailContent?> ParcelRequestAnsweredAsync(long requestId, string hello, TenantInfo tenant, CancellationToken cancellationToken)
    {
        var found = await (
            from request in db.ParcelRequests.IgnoreQueryFilters([QueryFilters.Merchant])
            join parcel in db.Parcels.IgnoreQueryFilters([QueryFilters.Merchant]) on request.ParcelId equals parcel.Id
            where request.Id == requestId && request.Status != ParcelRequestStatus.Open
            select new { request.Kind, request.Status, request.Answer, request.CodAmount, request.NewCodAmount, parcel.TrackingCode, parcel.RecipientName })
            .AsNoTracking()
            .SingleOrDefaultAsync(cancellationToken);
        if (found is null)
        {
            return null;
        }

        var approved = found.Status == ParcelRequestStatus.Approved;
        var currency = tenant.CurrencyCode;
        var what = found.Kind == ParcelRequestKind.Cancel
            ? "cancel the delivery"
            : $"change the cash to collect from {EmailLayout.Money(found.CodAmount, currency)} to {EmailLayout.Money(found.NewCodAmount ?? 0, currency)}";
        var outcome = (approved, found.Kind) switch
        {
            (true, ParcelRequestKind.Cancel) => "The delivery is stopped and the parcel is on its way back to you. It is charged as a return.",
            (true, _) => $"The rider now collects {EmailLayout.Money(found.NewCodAmount ?? 0, currency)}.",
            _ => "The parcel carries on as it was."
        };

        return new EmailContent
        {
            Subject = $"Your request for {found.TrackingCode} was {(approved ? "approved" : "not approved")}",
            Preview = outcome,
            Label = approved ? "Request approved" : "Request refused",
            Tone = approved ? EmailTone.Good : EmailTone.Warning,
            Title = approved ? "We approved your request" : "We could not approve your request",
            Greeting = hello,
            Paragraphs = [$"You asked us to {what} for parcel {found.TrackingCode} to {found.RecipientName}.", outcome],
            Note = found.Answer is null ? null : (approved ? "Our note" : "Our reason", found.Answer),
            Button = ($"Open parcel {found.TrackingCode}", links.Page($"Merchant/Parcel/{Uri.EscapeDataString(found.TrackingCode)}"))
        };
    }

    private async Task<EmailContent?> ReturnListAsync(long listId, string hello, TenantInfo tenant, CancellationToken cancellationToken)
    {
        var list = await db.ReturnLists
            .IgnoreQueryFilters([QueryFilters.Merchant])
            .AsNoTracking()
            .SingleOrDefaultAsync(l => l.Id == listId && l.Status == ReturnListStatus.HandedOver, cancellationToken);
        if (list is null)
        {
            // Confirmed already, or not handed over after all
            return null;
        }

        var parcels = await db.ReturnListParcels
            .IgnoreQueryFilters([QueryFilters.Merchant])
            .CountAsync(p => p.ReturnListId == listId, cancellationToken);
        var handed = list.HandedOverOn is { } on ? tenant.Local(on).ToString("d MMMM, h:mm tt", CultureInfo.InvariantCulture) : "today";

        return new EmailContent
        {
            Subject = $"Please confirm your returned parcels ({list.Number})",
            Preview = $"{parcels} parcel{(parcels == 1 ? "" : "s")} handed back to you. Check and confirm.",
            Label = "Returns handed back",
            Title = "Your returned parcels were handed over",
            Greeting = hello,
            Paragraphs =
            [
                "Our rider handed your returned parcels to your pickup point. Please check them and confirm the list, so we both have a record. If anything is missing, say so when you confirm and we will look into it."
            ],
            Highlight = (parcels.ToString(CultureInfo.InvariantCulture), $"parcel{(parcels == 1 ? "" : "s")} on return list {list.Number}"),
            Rows = [new EmailRow("Return list", list.Number), new EmailRow("Handed over", handed)],
            Button = ("Check and confirm", links.Page($"Merchant/Return/{Uri.EscapeDataString(list.Number)}"))
        };
    }

    private async Task<EmailContent?> PaymentAsync(long paymentId, string hello, TenantInfo tenant, bool refunded, CancellationToken cancellationToken)
    {
        var payment = await db.OnlinePayments
            .IgnoreQueryFilters([QueryFilters.Merchant])
            .AsNoTracking()
            .SingleOrDefaultAsync(
                p => p.Id == paymentId && p.ParcelId == null && p.Status == (refunded ? OnlinePaymentStatus.Refunded : OnlinePaymentStatus.Paid),
                cancellationToken);
        if (payment is null)
        {
            return null;
        }

        var amount = EmailLayout.Money(payment.PaidAmount ?? payment.Amount, payment.Currency);
        var method = OnlinePayment.MethodName(payment.Method);
        EmailRow[] rows =
        [
            new EmailRow("Payment", payment.Number),
            .. method is null ? Array.Empty<EmailRow>() : [new EmailRow("Paid by", method)],
            new EmailRow(refunded ? "Refunded" : "Credited to your balance", amount, Strong: true)
        ];

        return refunded
            ? new EmailContent
            {
                Subject = $"Your payment {payment.Number} was refunded",
                Preview = $"{amount} went back to the account you paid from.",
                Label = "Payment refunded",
                Title = "We refunded your payment",
                Greeting = hello,
                Paragraphs = [$"We refunded your online payment of {amount}. It goes back to the account you paid from; banks and wallets can take a few days to show it."],
                Rows = rows,
                Note = payment.Note is null ? null : ("Our note", payment.Note),
                Button = ("See your payments", links.Page("Merchant/Payments"))
            }
            : new EmailContent
            {
                Subject = $"Payment received: {amount} ({payment.Number})",
                Preview = "Thank you. It is on your balance now.",
                Label = "Payment received",
                Tone = EmailTone.Good,
                Title = "Thank you for your payment",
                Greeting = hello,
                Paragraphs = ["We received your online payment and added it to your balance with us. Keep this email as your receipt."],
                Highlight = (amount, "paid online"),
                Rows = rows,
                Button = ("See your payments", links.Page("Merchant/Payments"))
            };
    }

    private async Task<EmailContent?> AdjustmentAsync(long entryId, string hello, TenantInfo tenant, CancellationToken cancellationToken)
    {
        var entry = await db.LedgerEntries
            .IgnoreQueryFilters([QueryFilters.Merchant])
            .AsNoTracking()
            .SingleOrDefaultAsync(e => e.Id == entryId, cancellationToken);
        if (entry is null)
        {
            return null;
        }

        var credit = entry.Amount > 0;
        var amount = EmailLayout.Money(Math.Abs(entry.Amount), tenant.CurrencyCode);

        return new EmailContent
        {
            Subject = credit ? $"We added {amount} to your balance" : $"A charge of {amount} was added to your balance",
            Preview = entry.Note ?? "",
            Label = credit ? "Credit added" : "Charge added",
            Tone = credit ? EmailTone.Good : EmailTone.Warning,
            Title = credit ? $"{amount} added to your balance" : $"{amount} charged to your balance",
            Greeting = hello,
            Paragraphs =
            [
                credit
                    ? "We have credited your balance. It goes out to you with your next payout."
                    : "We have added a charge to your balance. It comes off the next cash we collect for you."
            ],
            Highlight = (EmailLayout.Signed(entry.Amount, tenant.CurrencyCode), entry.EntryDate.ToString("d MMMM yyyy", CultureInfo.InvariantCulture)),
            Note = entry.Note is null ? null : ("Why", entry.Note),
            Button = ("See your statement", links.Page("Merchant/Payments"))
        };
    }
}
