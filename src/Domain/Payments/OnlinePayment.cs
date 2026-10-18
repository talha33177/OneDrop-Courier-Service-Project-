using Domain.Common;
using Domain.Parcels;

namespace Domain.Payments;

/// <summary>Stored as TINYINT. Never renumber a value that has been saved.</summary>
public enum OnlinePaymentStatus : byte
{
    /// <summary>The merchant was sent to the gateway's payment page; the gateway has confirmed nothing yet.</summary>
    Started = 1,

    /// <summary>The gateway confirmed it and the merchant's balance was credited.</summary>
    Paid = 2,

    /// <summary>Cancelled, refused or never finished on the payment page. Still credited if the gateway confirms it later.</summary>
    Failed = 3,

    /// <summary>
    /// The gateway took the money but a person must look first: it marked the payment risky, or confirmed another amount
    /// than the one asked for. Credited only when the courier accepts it.
    /// </summary>
    Review = 4,

    /// <summary>
    /// The money went back to the payer through the gateway: a held payment the courier refunded instead of crediting,
    /// or a recipient's payment for a parcel that never reached them.
    /// </summary>
    Refunded = 5,

    /// <summary>
    /// A recipient's payment that must go back to them: the parcel was cancelled or returned, was paid at the door first,
    /// or the payment arrived for a parcel that could no longer take it. The check job asks the gateway to refund it.
    /// </summary>
    RefundDue = 6
}

/// <summary>What the gateway said of a refund: made, still being made (it gave a reference to ask about), or refused.</summary>
public enum RefundState
{
    Done,
    Processing,
    Refused
}

/// <summary>The smallest and largest payment the gateway takes.</summary>
public sealed record PaymentLimits(decimal Minimum, decimal Maximum);

/// <summary>
/// What the gateway says it took, as the gateway itself answered when asked by our server: never what a browser posted.
/// <see cref="Amount"/> is in <see cref="Currency"/>, the currency the payment was asked in; <see cref="StoreAmount"/> is
/// what reaches the courier once the gateway has taken its fee.
/// </summary>
public sealed record GatewayReceipt(
    string TransactionId,
    string ValidationId,
    decimal Amount,
    string Currency,
    decimal? StoreAmount,
    string? Method,
    string? BankTransactionId,
    bool Risky,
    string? RiskNote = null);

/// <summary>
/// Money taken online through the payment gateway, with its number (PAY-100001), for one of two things:
/// <list type="bullet">
/// <item>A merchant paying the courier what it owes (no <see cref="ParcelId"/>). The amount is the balance owed when it
/// was started. Once the gateway confirms the money, the payment writes one credit on the merchant's balance (an
/// adjustment line, so payouts, statements and reports already count it) and never a second: money the gateway
/// confirms is credited exactly once, whenever and however often the confirmation arrives, even after the payment page
/// said it failed.</item>
/// <item>A recipient paying a parcel's cash on delivery before it reaches the door (<see cref="ParcelId"/>). It writes no
/// line of its own: the parcel takes it (<c>Parcel.TakeOnlinePayment</c>), the rider collects nothing, and the
/// parcel's own lines pay the merchant when it is delivered. When the parcel cannot take it, or never reaches the
/// recipient, the payment is refunded through the gateway.</item>
/// </list>
/// The gateway knows it by <see cref="TransactionId"/>, a random value, so a payment can neither be guessed nor mistaken
/// for another database's.
/// </summary>
public class OnlinePayment : TenantEntity, IMerchantOwned
{
    public const int MaxNoteLength = 300;

    public const int TransactionIdLength = 30;

    private OnlinePayment()
    {
    }

    public long MerchantId { get; private set; }

    /// <summary>The parcel whose cash on delivery a recipient paid; null for a merchant paying what it owes.</summary>
    public long? ParcelId { get; private set; }

    public bool IsForParcel => ParcelId is not null;

    /// <summary>Where a recipient wants the receipt (and a refund's notice) sent; null for a merchant's own payment.</summary>
    public string? PayerEmail { get; private set; }

    /// <summary>The gateway's reference for the refund once asked for it; null before, or when it refused.</summary>
    public string? RefundReference { get; private set; }

    /// <summary>When the gateway was last asked to refund it, or about its refund.</summary>
    public DateTime? RefundTriedOn { get; private set; }

    /// <summary>The gateway refused to refund it: asked again later, while the courier sees why.</summary>
    public bool RefundRefused => Status == OnlinePaymentStatus.RefundDue && RefundReference is null && RefundTriedOn is not null;

    /// <summary>When the gateway said the money was back with the payer, or the courier recorded a refund it made by hand.</summary>
    public DateTime? RefundedOn { get; private set; }

    /// <summary>PAY-100001, from the database sequence on insert.</summary>
    public string Number { get; private set; } = null!;

    /// <summary>What the gateway knows the payment by (its tran_id).</summary>
    public string TransactionId { get; private set; } = "";

    /// <summary>What the merchant was asked to pay: its balance owed when it started.</summary>
    public decimal Amount { get; private set; }

    public string Currency { get; private set; } = "";

    public OnlinePaymentStatus Status { get; private set; }

    /// <summary>What the gateway confirmed, in <see cref="Currency"/>; null before, or when it was paid in another currency.</summary>
    public decimal? PaidAmount { get; private set; }

    /// <summary>What reaches the courier after the gateway's fee, as the gateway said.</summary>
    public decimal? StoreAmount { get; private set; }

    /// <summary>How it was paid, in the gateway's words (bKash, a card).</summary>
    public string? Method { get; private set; }

    /// <summary>The gateway's id for the confirmed payment (its val_id).</summary>
    public string? ValidationId { get; private set; }

    public string? BankTransactionId { get; private set; }

    /// <summary>Why it failed or waits for a check, or what the courier did with it.</summary>
    public string? Note { get; private set; }

    /// <summary>When the gateway's confirmation was recorded.</summary>
    public DateTime? ConfirmedOn { get; private set; }

    /// <summary>The credit it wrote on the merchant's balance; null until it is credited.</summary>
    public long? LedgerEntryId { get; private set; }

    public LedgerEntry? LedgerEntry { get; private set; }

    /// <summary>The browser coming back, the gateway's notice and the check job may record the answer at once: one wins.</summary>
    public byte[] RowVersion { get; private set; } = [];

    /// <summary>
    /// Starts paying <paramref name="owed"/>, what the merchant owes now: more than nothing, and within what the gateway
    /// takes.
    /// </summary>
    public static Result<OnlinePayment> Start(long merchantId, decimal owed, string currency, PaymentLimits limits)
    {
        if (owed <= 0)
        {
            return Error.Validation("payment.nothingOwed", "You owe the courier nothing, so there is nothing to pay.");
        }

        if (owed < limits.Minimum)
        {
            return Error.Validation(
                "payment.belowMinimum",
                $"Online payments start at ৳{limits.Minimum:N0}. The ৳{owed:N0} you owe comes off the next cash we collect for you.");
        }

        if (owed > limits.Maximum)
        {
            return Error.Validation(
                "payment.aboveMaximum",
                $"Online payments go up to ৳{limits.Maximum:N0}. Call the courier to settle the ৳{owed:N0} you owe.");
        }

        return new OnlinePayment
        {
            MerchantId = merchantId,
            TransactionId = Guid.NewGuid().ToString("N")[..TransactionIdLength],
            Amount = decimal.Round(owed, 2),
            Currency = currency,
            Status = OnlinePaymentStatus.Started
        };
    }

    /// <summary>
    /// Starts a recipient paying <paramref name="parcel"/>'s cash on delivery: while the parcel may still be paid online,
    /// and within what the gateway takes.
    /// </summary>
    public static Result<OnlinePayment> StartForParcel(Parcel parcel, string currency, PaymentLimits limits, string? payerEmail = null)
    {
        if (parcel.IsNew)
        {
            throw new InvalidOperationException("A parcel is paid for once it is booked.");
        }

        var payable = parcel.CanBePaidOnline();
        if (payable.IsFailure)
        {
            return payable.Error!;
        }

        if (parcel.CodAmount < limits.Minimum || parcel.CodAmount > limits.Maximum)
        {
            return Error.Validation(
                "payment.limits",
                $"Online payments take ৳{limits.Minimum:N0} to ৳{limits.Maximum:N0}. Pay this parcel in cash to the rider.");
        }

        return new OnlinePayment
        {
            MerchantId = parcel.MerchantId,
            ParcelId = parcel.Id,
            PayerEmail = Clip(payerEmail, 254),
            TransactionId = Guid.NewGuid().ToString("N")[..TransactionIdLength],
            Amount = parcel.CodAmount,
            Currency = currency,
            Status = OnlinePaymentStatus.Started
        };
    }

    /// <summary>
    /// The gateway confirmed the payment. When it is the amount asked for and the gateway saw no risk, a merchant's
    /// payment is credited at once and a recipient's is paid, for its parcel to take; otherwise it is held for the
    /// courier to check. Recording a confirmation again changes nothing.
    /// </summary>
    public void Confirm(GatewayReceipt receipt, DateOnly today, DateTime now)
    {
        if (IsNew || receipt.TransactionId != TransactionId)
        {
            throw new InvalidOperationException("A payment is confirmed by the gateway's answer for its own transaction, once saved.");
        }

        if (Status is not (OnlinePaymentStatus.Started or OnlinePaymentStatus.Failed))
        {
            return;
        }

        var sameCurrency = string.Equals(receipt.Currency, Currency, StringComparison.OrdinalIgnoreCase);
        PaidAmount = sameCurrency ? receipt.Amount : null;
        StoreAmount = receipt.StoreAmount;
        Method = Clip(receipt.Method, 50);
        ValidationId = receipt.ValidationId;
        BankTransactionId = Clip(receipt.BankTransactionId, 80);
        ConfirmedOn = now;
        if (!sameCurrency || receipt.Amount != Amount)
        {
            Status = OnlinePaymentStatus.Review;
            Note = $"The gateway confirmed {receipt.Amount:0.00} {receipt.Currency}, not the {Amount:0.00} {Currency} asked for.";

            return;
        }

        if (receipt.Risky)
        {
            Status = OnlinePaymentStatus.Review;
            Note = Clip($"The gateway marked the payment risky{(string.IsNullOrWhiteSpace(receipt.RiskNote) ? "" : $": {receipt.RiskNote.Trim()}")}.", MaxNoteLength);

            return;
        }

        Credit(Amount, today);
    }

    /// <summary>
    /// A recipient's payment its parcel could not take (already paid at the door, cancelled, the amount changed) or a
    /// parcel that will never reach the recipient: the money is owed back, and the check job asks the gateway for it.
    /// </summary>
    public void OweBack(string reason)
    {
        if (!IsForParcel)
        {
            throw new InvalidOperationException("A merchant's payment is credited, never owed back by the courier on its own.");
        }

        if (Status is not (OnlinePaymentStatus.Paid or OnlinePaymentStatus.Review))
        {
            return;
        }

        Status = OnlinePaymentStatus.RefundDue;
        Note = Clip(reason, MaxNoteLength);
    }

    /// <summary>What the gateway answered when asked to refund, or about a refund it was making.</summary>
    public void RefundAnswered(RefundState state, string? reference, string? reason, DateTime now)
    {
        if (Status != OnlinePaymentStatus.RefundDue)
        {
            return;
        }

        RefundTriedOn = now;
        switch (state)
        {
            case RefundState.Done:
                Status = OnlinePaymentStatus.Refunded;
                RefundReference = Clip(reference, 80) ?? RefundReference;
                RefundedOn = now;
                Raise(new OnlinePaymentRefunded(this));
                Note = Clip($"Refunded to the payer through the gateway{(RefundReference is null ? "" : $" ({RefundReference})")}.", MaxNoteLength);
                break;
            case RefundState.Processing:
                RefundReference = Clip(reference, 80) ?? RefundReference;
                break;
            default:
                // Asked again on the next check; the courier sees why meanwhile
                RefundReference = null;
                Note = Clip($"The gateway refused the refund: {reason ?? "it gave no reason"}", MaxNoteLength);
                break;
        }
    }

    /// <summary>The payment page said it failed or was cancelled. Only a payment still waiting is marked; a confirmed one stays.</summary>
    public void Fail(string? reason)
    {
        if (Status != OnlinePaymentStatus.Started)
        {
            return;
        }

        Status = OnlinePaymentStatus.Failed;
        Note = Clip(string.IsNullOrWhiteSpace(reason) ? "It was not finished on the payment page." : reason.Trim(), MaxNoteLength);
    }

    /// <summary>The courier checked a payment held for review and credits what the gateway took.</summary>
    public Result Accept(DateOnly today)
    {
        if (Status != OnlinePaymentStatus.Review)
        {
            return Error.Conflict("payment.notInReview", $"{Number} is not waiting for a check.");
        }

        if (PaidAmount is not { } paid || paid <= 0)
        {
            return Error.Validation("payment.currency", $"{Number} was not paid in {Currency}, so it cannot be credited. Refund it through the gateway.");
        }

        // The note keeps why it was held, for the record
        Credit(paid, today);

        return Result.Success();
    }

    /// <summary>
    /// The courier refunded a payment held for review, or one owed back that the gateway would not refund, through the
    /// gateway's own panel, and records it here.
    /// </summary>
    public Result Refund(string? note, DateTime now)
    {
        if (Status is not (OnlinePaymentStatus.Review or OnlinePaymentStatus.RefundDue))
        {
            return Error.Conflict("payment.notInReview", $"{Number} is not waiting for a check or a refund.");
        }

        var why = note?.Trim();
        if (string.IsNullOrEmpty(why) || why.Length > MaxNoteLength)
        {
            return Error.Validation("payment.note", $"Say what was refunded and why, in at most {MaxNoteLength} characters.");
        }

        Status = OnlinePaymentStatus.Refunded;
        RefundedOn = now;
        Note = why;
        Raise(new OnlinePaymentRefunded(this));

        return Result.Success();
    }

    /// <summary>
    /// How a payment was made, in words people use, from the gateway's label for it: SSLCommerz says "BKASH-BKash" or
    /// "VISA-Dutch Bangla", read as "bKash" or "Visa card, Dutch Bangla". A label it does not know is shown as it came.
    /// </summary>
    public static string? MethodName(string? method)
    {
        if (string.IsNullOrWhiteSpace(method))
        {
            return null;
        }

        var parts = method.Split('-', 2, StringSplitOptions.TrimEntries);
        var detail = parts.Length > 1 && parts[1].Length > 0 ? parts[1] : null;

        return parts[0].ToUpperInvariant() switch
        {
            "BKASH" => "bKash",
            "NAGAD" => "Nagad",
            "DBBLMOBILEB" or "ROCKET" => "Rocket",
            "UPAY" => "Upay",
            "VISA" => detail is null ? "Visa card" : $"Visa card, {detail}",
            "MASTER" or "MASTERCARD" => detail is null ? "Mastercard" : $"Mastercard, {detail}",
            "AMEX" => detail is null ? "American Express card" : $"American Express card, {detail}",
            _ => detail ?? method.Trim()
        };
    }

    private void Credit(decimal amount, DateOnly today)
    {
        if (IsForParcel)
        {
            // The parcel takes a recipient's payment and its own lines pay the merchant; nothing is credited here
            Status = OnlinePaymentStatus.Paid;
            Raise(new OnlinePaymentPaid(this));

            return;
        }

        var line = LedgerEntry.Adjust(MerchantId, amount, $"Paid online{(Method is null ? "" : $" by {MethodName(Method)}")}, {Number}", today);
        if (line.IsFailure)
        {
            throw new InvalidOperationException($"Payment {Number} could not be credited: {line.Error!.Message}");
        }

        LedgerEntry = line.Value;
        Status = OnlinePaymentStatus.Paid;
        Raise(new OnlinePaymentPaid(this));
    }

    private static string? Clip(string? value, int length)
    {
        var trimmed = value?.Trim();

        return string.IsNullOrEmpty(trimmed) ? null : trimmed.Length > length ? trimmed[..length] : trimmed;
    }
}

/// <summary>A payment was credited, or taken by its parcel: the payer gets a receipt by email.</summary>
public sealed record OnlinePaymentPaid(OnlinePayment Payment) : IDomainEvent;

/// <summary>A payment went back to the payer: the payer is told by email.</summary>
public sealed record OnlinePaymentRefunded(OnlinePayment Payment) : IDomainEvent;
