using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Application.Abstractions;
using Application.Common;
using Domain.Common;
using Domain.Parcels;
using Domain.Payments;

namespace Application.Payments.OnlinePayments;

/// <summary>
/// A parcel as its recipient sees it on the pay page, once they have given the phone number it is for: what to pay
/// (<see cref="Amount"/>), whether it is already paid, and why it cannot be paid online when it cannot.
/// </summary>
public sealed record ParcelToPay(
    string TrackingCode,
    string Merchant,
    string RecipientName,
    decimal Amount,
    bool Paid,
    string? CannotPay,
    string Gateway);

/// <summary>A recipient's payment as the pay page shows it after the gateway sent them back. No amount: the page is public.</summary>
public sealed record RecipientPaymentView(string Number, OnlinePaymentStatus Status, string? Note);

/// <summary>
/// A recipient pays a parcel's cash on delivery online before it reaches the door (4.10). The pay page is public, so it
/// shows the amount only to someone who gives the phone number the parcel is for, as the rider would ask at the door;
/// the amount paid is the parcel's own, never one the browser sends. The payment then follows the merchant's payments
/// through the gateway (<see cref="OnlinePaymentsHandler"/>): believed only when the gateway confirms it to our server,
/// taken by the parcel once, and refunded when the parcel cannot take it or never reaches the recipient.
/// </summary>
public class RecipientPaymentsHandler(
    IAppDbContext db,
    ITenantContext tenantContext,
    IPaymentGateway gateway,
    ITrackingLinks links,
    OnlinePaymentsHandler payments,
    ILogger<RecipientPaymentsHandler> logger)
{
    public static readonly Error NotFound = Error.NotFound(
        "pay.notFound",
        "We could not find a parcel with that tracking code for that phone number. Check both and try again.");

    public async Task<Result<ParcelToPay>> GetAsync(string? trackingCode, string? phone, CancellationToken cancellationToken = default)
    {
        var parcel = await FindAsync(trackingCode, phone, cancellationToken);
        if (parcel.IsFailure)
        {
            return parcel.Error!;
        }

        var p = parcel.Value;
        var merchant = await db.Merchants
            .IgnoreQueryFilters([QueryFilters.Merchant])
            .Where(m => m.Id == p.MerchantId)
            .Select(m => m.Name)
            .SingleAsync(cancellationToken);
        var payable = p.CanBePaidOnline();
        string? cannot = payable.IsFailure ? payable.Error!.Message
            : !gateway.IsAvailable ? "Online payments are not set up yet: pay the rider in cash."
            : p.CodAmount < gateway.Limits.Minimum || p.CodAmount > gateway.Limits.Maximum
                ? $"Online payments take ৳{gateway.Limits.Minimum:N0} to ৳{gateway.Limits.Maximum:N0}: pay the rider in cash."
                : null;

        return new ParcelToPay(p.TrackingCode, merchant, p.RecipientName, p.CodAmount, p.PaidOnline is not null, cannot, gateway.Name);
    }

    /// <summary>Opens a payment for the parcel's cash on delivery and returns the gateway's page to send the recipient to.</summary>
    public async Task<Result<PaymentStarted>> StartAsync(
        string? trackingCode,
        string? phone,
        string? email,
        CancellationToken cancellationToken = default)
    {
        var tenant = tenantContext.Require();
        if (!gateway.IsAvailable)
        {
            return Error.Conflict("payment.unavailable", "Online payments are not set up yet: pay the rider in cash.");
        }

        var given = email?.Trim();
        if (string.IsNullOrEmpty(given) || given.Length > 100 || !given.Contains('@') || given.Contains(' '))
        {
            return Error.Validation("payment.email", "Enter your email address: the payment page sends the receipt there.");
        }

        var found = await FindAsync(trackingCode, phone, cancellationToken);
        if (found.IsFailure)
        {
            return found.Error!;
        }

        var parcel = found.Value;
        var started = OnlinePayment.StartForParcel(parcel, tenant.CurrencyCode.Trim(), gateway.Limits, given);
        if (started.IsFailure)
        {
            return started.Error!;
        }

        var payment = started.Value;
        db.OnlinePayments.Add(payment);
        await db.SaveChangesAsync(cancellationToken);

        var city = await (
            from area in db.Areas
            join zone in db.Zones on area.ZoneId equals zone.Id
            where area.Id == parcel.AreaId
            select zone.City)
            .SingleAsync(cancellationToken);
        var session = new PaymentSession(
            payment.TransactionId,
            payment.Amount,
            payment.Currency,
            $"Cash on delivery for parcel {parcel.TrackingCode} ({payment.Number})",
            new PaymentPayer(
                parcel.RecipientName,
                given,
                PhoneNumber.Parse(parcel.RecipientPhone) is { IsSuccess: true } local ? local.Value.Local : parcel.RecipientPhone,
                parcel.RecipientAddress,
                city),
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

            return Error.Conflict("payment.gateway", $"{gateway.Name} would not open the payment: {exception.Message} Try again in a few minutes, or pay the rider in cash.");
        }
    }

    /// <summary>
    /// Payment <paramref name="number"/> of parcel <paramref name="trackingCode"/>, as it stands. One still waiting is
    /// asked about first, so a recipient coming back from the payment page reads the answer, not "waiting".
    /// </summary>
    public async Task<RecipientPaymentView?> PaymentAsync(string? trackingCode, string? number, CancellationToken cancellationToken = default)
    {
        var code = trackingCode?.Trim().ToUpperInvariant();
        var payment = await (
            from p in db.OnlinePayments.IgnoreQueryFilters([QueryFilters.Merchant])
            join parcel in db.Parcels.IgnoreQueryFilters([QueryFilters.Merchant]) on p.ParcelId equals parcel.Id
            where p.Number == number && parcel.TrackingCode == code
            select p)
            .SingleOrDefaultAsync(cancellationToken);
        if (payment is null)
        {
            return null;
        }

        if (payment.Status == OnlinePaymentStatus.Started && gateway.IsAvailable)
        {
            await payments.SettleAsync(payment, null, null, cancellationToken);
        }

        return new RecipientPaymentView(payment.Number, payment.Status, payment.Note);
    }

    /// <summary>
    /// The parcel with this tracking code, when <paramref name="phone"/> is the number it is for. Found whoever is signed
    /// in on this browser: the recipient is not the merchant.
    /// </summary>
    private async Task<Result<Parcel>> FindAsync(string? trackingCode, string? phone, CancellationToken cancellationToken)
    {
        var code = trackingCode?.Trim().ToUpperInvariant();
        if (string.IsNullOrEmpty(code) || PhoneNumber.Parse(phone) is not { IsSuccess: true } given)
        {
            return NotFound;
        }

        var number = given.Value.Value;
        var parcel = await db.Parcels
            .IgnoreQueryFilters([QueryFilters.Merchant])
            .SingleOrDefaultAsync(p => p.TrackingCode == code && p.RecipientPhone == number, cancellationToken);

        return parcel is null ? NotFound : parcel;
    }
}
