using Domain.Payments;

namespace Application.Abstractions;

/// <summary>Who pays, as the gateway's payment page asks for it.</summary>
public sealed record PaymentPayer(string Name, string Email, string Phone, string Address, string City);

/// <summary>
/// A payment to take: what the gateway will know it by, how much, what it is for, who pays, and where the gateway sends
/// the payer back (<paramref name="SuccessUrl"/>, <paramref name="FailUrl"/>, <paramref name="CancelUrl"/>) and posts
/// its own notice (<paramref name="NoticeUrl"/>).
/// </summary>
public sealed record PaymentSession(
    string TransactionId,
    decimal Amount,
    string Currency,
    string Description,
    PaymentPayer Payer,
    string SuccessUrl,
    string FailUrl,
    string CancelUrl,
    string NoticeUrl);

public enum GatewayState
{
    /// <summary>Nothing final yet: the payer has not finished, or the gateway has no record of it.</summary>
    Pending,

    Paid,

    Failed
}

/// <summary>What the gateway knows of one transaction: paid (with its receipt), failed (with why), or nothing final yet.</summary>
public sealed record GatewayLookup(GatewayState State, GatewayReceipt? Receipt = null, string? Reason = null);

/// <summary>
/// Adapter for taking a payment online (SSLCommerz): the payer is sent to the gateway's own page, comes back to one of
/// our addresses, and the payment is then confirmed by asking the gateway itself, never by trusting what the browser
/// brought back. The app ships a fake for Development and the tests, whose payment page is <c>/Dev/Pay</c>.
/// </summary>
public interface IPaymentGateway
{
    /// <summary>The gateway as people know it ("SSLCommerz").</summary>
    string Name { get; }

    /// <summary>False when no gateway is set up: online payments are not offered.</summary>
    bool IsAvailable { get; }

    PaymentLimits Limits { get; }

    /// <summary>
    /// Opens the payment and returns the address of the gateway's payment page, or throws
    /// <see cref="PaymentGatewayException"/> with why it would not.
    /// </summary>
    Task<string> StartAsync(PaymentSession session, CancellationToken cancellationToken = default);

    /// <summary>
    /// The payment the gateway confirms under <paramref name="validationId"/> (the id it hands back on success), or null
    /// when it confirms none.
    /// </summary>
    Task<GatewayReceipt?> ValidateAsync(string validationId, CancellationToken cancellationToken = default);

    /// <summary>Asks the gateway what became of <paramref name="transactionId"/>.</summary>
    Task<GatewayLookup> LookUpAsync(string transactionId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Asks the gateway to send a confirmed payment back to the payer: <paramref name="bankTransactionId"/> is the
    /// gateway's id for the payment, <paramref name="reference"/> ours (the payment's number).
    /// </summary>
    Task<RefundAnswer> RefundAsync(
        string bankTransactionId,
        decimal amount,
        string reference,
        string remarks,
        CancellationToken cancellationToken = default);

    /// <summary>What became of a refund the gateway said it was making, by the reference it gave.</summary>
    Task<RefundAnswer> RefundStatusAsync(string refundReference, CancellationToken cancellationToken = default);
}

/// <summary>The gateway's answer about a refund, with its reference for it and, when refused, why.</summary>
public sealed record RefundAnswer(RefundState State, string? Reference = null, string? Reason = null);

/// <summary>The gateway refused or could not be reached; <see cref="Exception.Message"/> says why, in words a person can act on.</summary>
public class PaymentGatewayException(string message, Exception? inner = null) : Exception(message, inner);
