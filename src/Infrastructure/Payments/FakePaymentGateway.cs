using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Application.Abstractions;
using Domain.Payments;

namespace Infrastructure.Payments;

/// <summary>A payment opened with the fake gateway, and what became of it on its page (<c>/Dev/Pay</c>).</summary>
public sealed record FakeCharge(PaymentSession Session, DateTime StartedOn)
{
    public GatewayState State { get; init; } = GatewayState.Pending;

    public string? ValidationId { get; init; }

    public string? Method { get; init; }

    public bool Risky { get; init; }

    /// <summary>What the payer paid, when a test makes it differ from what was asked.</summary>
    public decimal? PaidAmount { get; init; }

    public string? Reason { get; init; }
}

/// <summary>A refund the fake gateway made.</summary>
public sealed record FakeRefund(string BankTransactionId, decimal Amount, string Reference, string RefundReference);

/// <summary>The payments the fake gateway was asked to take, by transaction, so the Dev page and the tests can pay or fail them.</summary>
public class FakePaymentLog
{
    private readonly ConcurrentDictionary<string, FakeCharge> charges = new();

    public IReadOnlyList<FakeCharge> Recent => [.. charges.Values.OrderByDescending(c => c.StartedOn).Take(50)];

    public void Add(FakeCharge charge)
    {
        charges[charge.Session.TransactionId] = charge;
    }

    public FakeCharge? Find(string transactionId)
    {
        return charges.GetValueOrDefault(transactionId);
    }

    public FakeCharge? FindPaid(string validationId)
    {
        return charges.Values.FirstOrDefault(c => c.State == GatewayState.Paid && c.ValidationId == validationId);
    }

    /// <summary>The payer pays on the page: returns the validation id the gateway hands back, the same however often it is paid.</summary>
    public string? Pay(string transactionId, string method, bool risky = false, decimal? amount = null)
    {
        var paid = charges.AddOrUpdate(
            transactionId,
            _ => throw new InvalidOperationException($"No payment {transactionId} was opened."),
            (_, charge) => charge.State == GatewayState.Paid
                ? charge
                : charge with
                {
                    State = GatewayState.Paid,
                    ValidationId = $"FAKE-VAL-{Guid.NewGuid():N}"[..30],
                    Method = method,
                    Risky = risky,
                    PaidAmount = amount
                });

        return paid.ValidationId;
    }

    /// <summary>Refunds the gateway made, newest last: the gateway's payment id, the amount and our reference.</summary>
    public IReadOnlyList<FakeRefund> Refunds => [.. refunds];

    private readonly ConcurrentQueue<FakeRefund> refunds = new();

    /// <summary>
    /// Sends a payment back. The gateway refuses a second refund of the same payment, as the real one refuses a refund
    /// larger than what is left of it.
    /// </summary>
    public RefundAnswer Refund(string bankTransactionId, decimal amount, string reference)
    {
        if (refunds.Any(r => r.BankTransactionId == bankTransactionId))
        {
            return new RefundAnswer(RefundState.Refused, Reason: "This payment was already refunded.");
        }

        var refund = new FakeRefund(bankTransactionId, amount, reference, $"FAKE-REF-{Guid.NewGuid():N}"[..20]);
        refunds.Enqueue(refund);

        return new RefundAnswer(RefundState.Done, refund.RefundReference);
    }

    /// <summary>The payment fails or is cancelled on the page.</summary>
    public void Fail(string transactionId, string reason)
    {
        charges.AddOrUpdate(
            transactionId,
            _ => throw new InvalidOperationException($"No payment {transactionId} was opened."),
            (_, charge) => charge.State == GatewayState.Paid ? charge : charge with { State = GatewayState.Failed, Reason = reason });
    }
}

/// <summary>
/// Stand-in for SSLCommerz in Development and the tests: its payment page is <c>/Dev/Pay/{transaction}</c> on the
/// courier's own site, where a payment is paid, made risky, failed or cancelled, and then the payer is sent back exactly
/// as the real gateway would send them. It takes no fee.
/// </summary>
public class FakePaymentGateway(FakePaymentLog log, SslcommerzOptions options, TimeProvider time, ILogger<FakePaymentGateway> logger)
    : IPaymentGateway
{
    public string Name => "the test gateway";

    public bool IsAvailable => true;

    public PaymentLimits Limits => options.Limits;

    public Task<string> StartAsync(PaymentSession session, CancellationToken cancellationToken = default)
    {
        log.Add(new FakeCharge(session, time.GetUtcNow().UtcDateTime));
        logger.LogInformation("Fake payment {TransactionId} opened for {Amount} {Currency}", session.TransactionId, session.Amount, session.Currency);

        return Task.FromResult(new Uri(new Uri(session.SuccessUrl), "/Dev/Pay/" + Uri.EscapeDataString(session.TransactionId)).ToString());
    }

    public Task<GatewayReceipt?> ValidateAsync(string validationId, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(log.FindPaid(validationId) is { } charge ? Receipt(charge) : null);
    }

    public Task<GatewayLookup> LookUpAsync(string transactionId, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(log.Find(transactionId) switch
        {
            { State: GatewayState.Paid } paid => new GatewayLookup(GatewayState.Paid, Receipt(paid)),
            { State: GatewayState.Failed } failed => new GatewayLookup(GatewayState.Failed, Reason: failed.Reason),
            _ => new GatewayLookup(GatewayState.Pending)
        });
    }

    public Task<RefundAnswer> RefundAsync(
        string bankTransactionId,
        decimal amount,
        string reference,
        string remarks,
        CancellationToken cancellationToken = default)
    {
        logger.LogInformation("Fake refund of {Amount} for {Reference}: {Remarks}", amount, reference, remarks);

        return Task.FromResult(log.Refund(bankTransactionId, amount, reference));
    }

    public Task<RefundAnswer> RefundStatusAsync(string refundReference, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(log.Refunds.Any(r => r.RefundReference == refundReference)
            ? new RefundAnswer(RefundState.Done, refundReference)
            : new RefundAnswer(RefundState.Refused, refundReference, "No such refund."));
    }

    private static GatewayReceipt Receipt(FakeCharge charge)
    {
        var amount = charge.PaidAmount ?? charge.Session.Amount;

        return new GatewayReceipt(
            charge.Session.TransactionId,
            charge.ValidationId!,
            amount,
            charge.Session.Currency,
            amount,
            charge.Method,
            $"FAKE-BANK-{charge.Session.TransactionId[..8]}",
            charge.Risky,
            charge.Risky ? "Marked risky on the test page" : null);
    }
}
