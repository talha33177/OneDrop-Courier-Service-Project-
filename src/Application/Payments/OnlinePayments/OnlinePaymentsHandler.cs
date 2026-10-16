using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Application.Abstractions;
using Application.Common;
using Domain.Common;
using Domain.Parcels;
using Domain.Payments;

namespace Application.Payments.OnlinePayments;

/// <summary>An online payment as a list shows it. Times are the tenant's.</summary>
public sealed record OnlinePaymentRow(
    string Number,
    long MerchantId,
    string Merchant,
    decimal Amount,
    OnlinePaymentStatus Status,
    decimal? PaidAmount,
    decimal? StoreAmount,
    string? Method,
    string? BankTransactionId,
    string? Note,
    DateTime Created,
    DateTime? ConfirmedOn)
{
    /// <summary>The parcel a recipient paid for; null for a merchant paying what it owes.</summary>
    public string? TrackingCode { get; init; }

    /// <summary>Owed back to the payer, and the gateway refused the last ask: the courier may refund it by hand.</summary>
    public bool RefundRefused { get; init; }
}

/// <summary>Where a payer coming back from the gateway lands: a recipient on its parcel's page, a merchant on its payments.</summary>
public sealed record PaymentReturned(string Number, string? TrackingCode);

/// <summary>The gateway's page to send the merchant to, for payment <paramref name="Number"/>.</summary>
public sealed record PaymentStarted(string Number, string PaymentPageUrl);

/// <summary>What the merchant's payments page needs to offer paying online: the gateway, its limits and the latest payments.</summary>
public sealed record MerchantOnlinePayments(string Gateway, bool Available, PaymentLimits Limits, IReadOnlyList<OnlinePaymentRow> Recent);

/// <summary>
/// The courier's view: payments held for a check, recipients' payments owed back (refunded by the check job, or by hand
/// when the gateway refuses), and the latest payments of both kinds.
/// </summary>
public sealed record AdminOnlinePayments(
    string Gateway,
    bool Available,
    IReadOnlyList<OnlinePaymentRow> ToCheck,
    IReadOnlyList<OnlinePaymentRow> Recent)
{
    public IReadOnlyList<OnlinePaymentRow> RefundsDue { get; init; } = [];
}

/// <summary>
/// A merchant pays the courier what it owes, online. <see cref="StartAsync"/> opens a payment for the balance owed now
/// and returns the gateway's page. The answer comes back three ways: the payer's browser returning
/// (<see cref="ReturnedAsync"/>), the gateway's own notice (<see cref="NoticeAsync"/>), and the check job
/// (<see cref="CheckAsync"/>) for a browser that never came back. Each one asks the gateway itself before anything is
/// recorded, so a forged return credits nothing, and the payment's rules credit confirmed money exactly once.
/// </summary>
public class OnlinePaymentsHandler(
    IAppDbContext db,
    ITenantContext tenantContext,
    ICurrentUser currentUser,
    IPaymentGateway gateway,
    ITrackingLinks links,
    IUserAccounts accounts,
    TimeProvider time,
    ILogger<OnlinePaymentsHandler> logger)
{
    /// <summary>The check job's name in the scheduler.</summary>
    public const string RecurringId = "online-payments";

    public static readonly Error NotFound = Error.NotFound("payment.notFound", "That payment was not found.");

    /// <summary>A payment still waiting this long after it started was never finished on the payment page.</summary>
    private static readonly TimeSpan GiveUpAfter = TimeSpan.FromDays(1);

    /// <summary>A failed payment is asked about again for this long, in case the gateway confirms it after all.</summary>
    private static readonly TimeSpan RecheckFailedFor = TimeSpan.FromDays(1);

    public async Task<MerchantOnlinePayments> ForMerchantAsync(CancellationToken cancellationToken = default)
    {
        var recent = await RowsAsync(db.OnlinePayments.Where(p => p.ParcelId == null).OrderByDescending(p => p.Id).Take(10), cancellationToken);

        return new MerchantOnlinePayments(gateway.Name, gateway.IsAvailable, gateway.Limits, recent);
    }

    /// <summary>
    /// The merchant's own payment <paramref name="number"/>, as it stands. One still waiting is asked about first, so a
    /// merchant coming back from the payment page reads the answer, not "waiting".
    /// </summary>
    public async Task<OnlinePaymentRow?> PaymentAsync(string? number, CancellationToken cancellationToken = default)
    {
        var payment = await db.OnlinePayments.SingleOrDefaultAsync(p => p.Number == number && p.ParcelId == null, cancellationToken);
        if (payment is null)
        {
            return null;
        }

        if (payment.Status == OnlinePaymentStatus.Started && gateway.IsAvailable)
        {
            await SettleAsync(payment, null, null, cancellationToken);
        }

        return (await RowsAsync(db.OnlinePayments.Where(p => p.Id == payment.Id), cancellationToken)).Single();
    }

    /// <summary>
    /// Opens a payment for what the merchant signed in owes now and returns the gateway's page to send it to. The amount
    /// is worked out here from the merchant's own lines, never taken from the browser.
    /// </summary>
    public async Task<Result<PaymentStarted>> StartAsync(CancellationToken cancellationToken = default)
    {
        var tenant = tenantContext.Require();
        var merchantId = currentUser.MerchantId ?? throw new InvalidOperationException("A merchant pays what it owes.");
        if (!gateway.IsAvailable)
        {
            return Error.Conflict("payment.unavailable", "Online payments are not set up yet. Call the courier to settle what you owe.");
        }

        var merchant = await db.Merchants.AsNoTracking().SingleAsync(m => m.Id == merchantId, cancellationToken);
        var email = merchant.ContactEmail ?? await accounts.MerchantEmailAsync(merchant.AccountId, cancellationToken);
        if (string.IsNullOrWhiteSpace(email))
        {
            return Error.Validation("payment.email", "The payment page needs an email address. Add one to your profile in Settings first.");
        }

        var balance = await db.LedgerEntries
            .Where(line => line.MerchantId == merchantId && line.PayoutId == null)
            .SumAsync(line => line.Amount, cancellationToken);
        var started = OnlinePayment.Start(merchantId, -balance, tenant.CurrencyCode.Trim(), gateway.Limits);
        if (started.IsFailure)
        {
            return started.Error!;
        }

        var payment = started.Value;
        db.OnlinePayments.Add(payment);
        await db.SaveChangesAsync(cancellationToken);

        var city = await (
            from point in db.PickupPoints
            join area in db.Areas on point.AreaId equals area.Id
            join zone in db.Zones on area.ZoneId equals zone.Id
            where point.MerchantId == merchantId && point.IsDefault && !point.Archived
            orderby point.Id
            select zone.City)
            .FirstOrDefaultAsync(cancellationToken);
        var session = new PaymentSession(
            payment.TransactionId,
            payment.Amount,
            payment.Currency,
            $"Balance owed to {tenant.Name} ({payment.Number})",
            new PaymentPayer(
                merchant.OwnerName.Length > 0 ? merchant.OwnerName : merchant.Name,
                email,
                PhoneNumber.Parse(merchant.ContactPhone) is { IsSuccess: true } phone ? phone.Value.Local : merchant.ContactPhone,
                merchant.Address,
                city ?? merchant.Address),
            links.PaymentReturn(payment.TransactionId, "success"),
            links.PaymentReturn(payment.TransactionId, "fail"),
            links.PaymentReturn(payment.TransactionId, "cancel"),
            links.PaymentNotice());
        try
        {
            return new PaymentStarted(payment.Number, await gateway.StartAsync(session, cancellationToken));
        }
        catch (PaymentGatewayException exception)
        {
            logger.LogWarning(exception, "{Gateway} would not open payment {Number}", gateway.Name, payment.Number);
            payment.Fail($"{gateway.Name} would not open the payment: {exception.Message}");
            await db.SaveChangesAsync(cancellationToken);

            return Error.Conflict("payment.gateway", $"{gateway.Name} would not open the payment: {exception.Message} Try again in a few minutes.");
        }
    }

    /// <summary>
    /// The payer's browser came back from the payment page, as <paramref name="outcome"/> (success, fail or cancel), with
    /// the gateway's <paramref name="validationId"/> on success. Nothing it brings is believed: the gateway is asked. The
    /// browser comes back without the merchant's sign-in (it is a post from the gateway's site), so the payment is found
    /// by its transaction, whichever business is open. Returns the payment's number and, for a recipient's payment, the
    /// parcel's tracking code; null for an unknown transaction.
    /// </summary>
    public async Task<PaymentReturned?> ReturnedAsync(string transactionId, string outcome, string? validationId, CancellationToken cancellationToken = default)
    {
        var payment = await ByTransactionAsync(transactionId, cancellationToken);
        if (payment is null)
        {
            return null;
        }

        await SettleAsync(
            payment,
            outcome == "success" ? validationId : null,
            outcome switch
            {
                "cancel" => "You cancelled it on the payment page.",
                "fail" => "The payment page said it failed.",
                _ => null
            },
            cancellationToken);

        var trackingCode = payment.ParcelId is { } parcelId
            ? await db.Parcels
                .IgnoreQueryFilters([QueryFilters.Merchant])
                .Where(p => p.Id == parcelId)
                .Select(p => p.TrackingCode)
                .SingleAsync(cancellationToken)
            : null;

        return new PaymentReturned(payment.Number, trackingCode);
    }

    /// <summary>The gateway's own notice of a payment, server to server. Checked with the gateway like a return.</summary>
    public async Task NoticeAsync(string? transactionId, string? validationId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(transactionId) || await ByTransactionAsync(transactionId, cancellationToken) is not { } payment)
        {
            return;
        }

        await SettleAsync(payment, validationId, null, cancellationToken);
    }

    /// <summary>
    /// The check job: asks the gateway about every payment still waiting, and every one that failed in the last day, so
    /// money taken from a payer whose browser never came back is still credited. A payment still waiting a day after it
    /// started was never finished, and is closed as failed.
    /// </summary>
    public async Task<int> CheckAsync(CancellationToken cancellationToken = default)
    {
        if (!gateway.IsAvailable)
        {
            return 0;
        }

        var now = time.GetUtcNow().UtcDateTime;
        var since = now - RecheckFailedFor;
        var open = await db.OnlinePayments
            .IgnoreQueryFilters([QueryFilters.Merchant])
            .Where(p => p.Status == OnlinePaymentStatus.Started || (p.Status == OnlinePaymentStatus.Failed && p.Created >= since))
            .OrderBy(p => p.Id)
            .ToListAsync(cancellationToken);
        var credited = 0;
        foreach (var payment in open)
        {
            await SettleAsync(
                payment,
                null,
                payment.Created < now - GiveUpAfter ? "It was not finished on the payment page within a day." : null,
                cancellationToken);
            if (payment.Status == OnlinePaymentStatus.Paid)
            {
                credited++;
            }
        }

        logger.LogInformation("Checked {Count} online payments with {Gateway}: {Credited} credited", open.Count, gateway.Name, credited);
        await RefundAsync(now, cancellationToken);

        return credited;
    }

    /// <summary>
    /// The refunds part of the check: a recipient's payment whose parcel was cancelled or came back is owed back, and
    /// every payment owed back is refunded through the gateway, or asked about while the gateway is making the refund.
    /// One the gateway refused is asked for again after <see cref="RetryRefusedRefundAfter"/>; the courier sees it meanwhile.
    /// </summary>
    private async Task RefundAsync(DateTime now, CancellationToken cancellationToken)
    {
        var undelivered = await (
            from payment in db.OnlinePayments.IgnoreQueryFilters([QueryFilters.Merchant])
            join parcel in db.Parcels.IgnoreQueryFilters([QueryFilters.Merchant]) on payment.ParcelId equals parcel.Id
            where payment.Status == OnlinePaymentStatus.Paid &&
                (parcel.Status == ParcelStatus.Cancelled || parcel.Status == ParcelStatus.Returned)
            select new { payment, parcel.TrackingCode, parcel.Status })
            .ToListAsync(cancellationToken);
        foreach (var owed in undelivered)
        {
            owed.payment.OweBack(
                $"{owed.TrackingCode} was {(owed.Status == ParcelStatus.Cancelled ? "cancelled" : "returned to the merchant")}, so the payment goes back to the recipient.");
        }

        if (undelivered.Count > 0)
        {
            await db.SaveChangesAsync(cancellationToken);
        }

        var retryBefore = now - RetryRefusedRefundAfter;
        var due = await db.OnlinePayments
            .IgnoreQueryFilters([QueryFilters.Merchant])
            .Where(p => p.Status == OnlinePaymentStatus.RefundDue &&
                (p.RefundReference != null || p.RefundTriedOn == null || p.RefundTriedOn < retryBefore))
            .OrderBy(p => p.Id)
            .ToListAsync(cancellationToken);
        foreach (var payment in due)
        {
            if (payment.BankTransactionId is not { } bankTransaction)
            {
                payment.RefundAnswered(RefundState.Refused, null, "the gateway gave no id for the payment; refund it from the gateway's panel", now);
                await db.SaveChangesAsync(cancellationToken);

                continue;
            }

            RefundAnswer answer;
            try
            {
                answer = payment.RefundReference is { } reference
                    ? await gateway.RefundStatusAsync(reference, cancellationToken)
                    : await gateway.RefundAsync(
                        bankTransaction,
                        payment.PaidAmount ?? payment.Amount,
                        payment.Number,
                        $"{tenantContext.Require().Name}: {payment.Note}",
                        cancellationToken);
            }
            catch (PaymentGatewayException exception)
            {
                // Not knowing whether it was made, nothing is recorded: the next check asks again, and the gateway refuses
                // to refund more than was paid
                logger.LogWarning(exception, "Could not ask {Gateway} to refund payment {Number}", gateway.Name, payment.Number);

                continue;
            }

            payment.RefundAnswered(answer.State, answer.Reference, answer.Reason, now);
            await db.SaveChangesAsync(cancellationToken);
            logger.LogInformation("Refund of payment {Number}: {State}", payment.Number, answer.State);
        }
    }

    /// <summary>A refund the gateway refused is asked for again after this long.</summary>
    private static readonly TimeSpan RetryRefusedRefundAfter = TimeSpan.FromHours(1);

    public async Task<AdminOnlinePayments> ForCourierAsync(CancellationToken cancellationToken = default)
    {
        return new AdminOnlinePayments(
            gateway.Name,
            gateway.IsAvailable,
            await RowsAsync(db.OnlinePayments.Where(p => p.Status == OnlinePaymentStatus.Review), cancellationToken),
            await RowsAsync(db.OnlinePayments.OrderByDescending(p => p.Id).Take(15), cancellationToken))
        {
            RefundsDue = await RowsAsync(db.OnlinePayments.Where(p => p.Status == OnlinePaymentStatus.RefundDue), cancellationToken)
        };
    }

    /// <summary>The courier checked a payment the gateway took but held, and credits it to the merchant.</summary>
    public async Task<Result> AcceptAsync(string? number, CancellationToken cancellationToken = default)
    {
        var payment = await db.OnlinePayments.SingleOrDefaultAsync(p => p.Number == number, cancellationToken);
        if (payment is null)
        {
            return NotFound;
        }

        var accepted = payment.Accept(tenantContext.Require().Today(time.GetUtcNow().UtcDateTime));
        if (accepted.IsSuccess)
        {
            await ToParcelAsync(payment, cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
        }

        return accepted;
    }

    /// <summary>
    /// The courier refunded a held payment, or one owed back that the gateway would not refund, through the gateway's
    /// own panel, and records it here.
    /// </summary>
    public async Task<Result> RefundAsync(string? number, string? note, CancellationToken cancellationToken = default)
    {
        var payment = await db.OnlinePayments.SingleOrDefaultAsync(p => p.Number == number, cancellationToken);
        if (payment is null)
        {
            return NotFound;
        }

        var refunded = payment.Refund(note, time.GetUtcNow().UtcDateTime);
        if (refunded.IsSuccess)
        {
            await db.SaveChangesAsync(cancellationToken);
        }

        return refunded;
    }

    /// <summary>
    /// Asks the gateway what became of <paramref name="payment"/> and records the answer: confirmed money is credited
    /// (or held for a check), a failure is kept with why. <paramref name="validationId"/> is the id the gateway handed
    /// back on success, checked with the gateway; it counts only for this payment's own transaction.
    /// <paramref name="failure"/> is what the payment page said when it sent the payer back without paying, recorded when
    /// the gateway confirms nothing. A gateway that cannot be reached changes nothing: the check job asks again.
    /// </summary>
    internal async Task SettleAsync(OnlinePayment payment, string? validationId, string? failure, CancellationToken cancellationToken)
    {
        if (payment.Status is not (OnlinePaymentStatus.Started or OnlinePaymentStatus.Failed))
        {
            return;
        }

        GatewayLookup answer;
        try
        {
            answer = !string.IsNullOrWhiteSpace(validationId)
                && await gateway.ValidateAsync(validationId, cancellationToken) is { } receipt
                && receipt.TransactionId == payment.TransactionId
                    ? new GatewayLookup(GatewayState.Paid, receipt)
                    : await gateway.LookUpAsync(payment.TransactionId, cancellationToken);
        }
        catch (PaymentGatewayException exception)
        {
            logger.LogWarning(exception, "Could not ask {Gateway} about payment {Number}", gateway.Name, payment.Number);
            answer = new GatewayLookup(GatewayState.Pending);
        }

        var now = time.GetUtcNow().UtcDateTime;
        switch (answer.State)
        {
            case GatewayState.Paid:
                payment.Confirm(answer.Receipt!, tenantContext.Require().Today(now), now);
                await ToParcelAsync(payment, cancellationToken);
                break;
            case GatewayState.Failed:
                payment.Fail(answer.Reason ?? failure);
                break;
            case GatewayState.Pending when failure is not null:
                payment.Fail(failure);
                break;
            default:
                return;
        }

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            // The browser, the gateway's notice or the check job recorded it at the same moment; theirs stands, and this
            // copy, with the credit it would have written, is let go so nothing saves it later
            if (payment.LedgerEntry is { } line)
            {
                db.Entry(line).State = EntityState.Detached;
            }

            if (payment.ParcelId is { } parcelId && db.Parcels.Local.SingleOrDefault(p => p.Id == parcelId) is { } parcel)
            {
                foreach (var added in parcel.Events.Where(e => db.Entry(e).State == EntityState.Added).ToList())
                {
                    db.Entry(added).State = EntityState.Detached;
                }

                db.Entry(parcel).State = EntityState.Detached;
            }

            db.Entry(payment).State = EntityState.Detached;

            return;
        }

        if (payment.Status == OnlinePaymentStatus.Paid)
        {
            logger.LogInformation("Payment {Number} of {Amount} credited to merchant {MerchantId}", payment.Number, payment.Amount, payment.MerchantId);
        }
    }

    /// <summary>
    /// A recipient's payment the gateway has just confirmed goes to its parcel, which the rider then delivers without
    /// collecting; when the parcel can no longer take it (paid at the door first, cancelled, returned), it is owed back.
    /// Saved with the payment, so the two never disagree.
    /// </summary>
    private async Task ToParcelAsync(OnlinePayment payment, CancellationToken cancellationToken)
    {
        if (payment is not { ParcelId: { } parcelId, Status: OnlinePaymentStatus.Paid })
        {
            return;
        }

        // The payer is not the merchant: the parcel is found by the payment, whoever is signed in
        var parcel = await db.Parcels
            .IgnoreQueryFilters([QueryFilters.Merchant])
            .SingleAsync(p => p.Id == parcelId, cancellationToken);
        var taken = parcel.TakeOnlinePayment(payment.PaidAmount ?? payment.Amount, payment.Number);
        if (taken.IsFailure)
        {
            payment.OweBack(taken.Error!.Message + " The payment goes back to the recipient.");
        }
    }

    /// <summary>
    /// The payment the gateway knows by <paramref name="transactionId"/>. Found whoever is signed in, or nobody: the
    /// gateway names the transaction, not the person.
    /// </summary>
    private Task<OnlinePayment?> ByTransactionAsync(string transactionId, CancellationToken cancellationToken)
    {
        return db.OnlinePayments
            .IgnoreQueryFilters([QueryFilters.Merchant])
            .SingleOrDefaultAsync(p => p.TransactionId == transactionId, cancellationToken);
    }

    /// <summary><paramref name="payments"/> as rows, the latest first, with the tenant's times.</summary>
    private async Task<IReadOnlyList<OnlinePaymentRow>> RowsAsync(IQueryable<OnlinePayment> payments, CancellationToken cancellationToken)
    {
        var tenant = tenantContext.Require();
        var rows = await (
            from payment in payments
            join merchant in db.Merchants on payment.MerchantId equals merchant.Id
            orderby payment.Id descending
            select new OnlinePaymentRow(
                payment.Number,
                payment.MerchantId,
                merchant.Name,
                payment.Amount,
                payment.Status,
                payment.PaidAmount,
                payment.StoreAmount,
                payment.Method,
                payment.BankTransactionId,
                payment.Note,
                payment.Created,
                payment.ConfirmedOn)
            {
                TrackingCode = db.Parcels.Where(p => p.Id == payment.ParcelId).Select(p => p.TrackingCode).FirstOrDefault(),
                RefundRefused = payment.Status == OnlinePaymentStatus.RefundDue && payment.RefundReference == null && payment.RefundTriedOn != null
            })
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        return [.. rows.Select(r => r with
        {
            Method = OnlinePayment.MethodName(r.Method),
            Created = tenant.Local(r.Created),
            ConfirmedOn = r.ConfirmedOn is { } confirmed ? tenant.Local(confirmed) : null
        })];
    }
}

/// <summary>Every few minutes: asks the gateway about online payments whose answer has not come back (see <see cref="OnlinePaymentsHandler.CheckAsync"/>).</summary>
public class CheckOnlinePaymentsJob(OnlinePaymentsHandler payments) : ITenantJob
{
    public Task RunAsync(CancellationToken cancellationToken)
    {
        return payments.CheckAsync(cancellationToken);
    }
}
