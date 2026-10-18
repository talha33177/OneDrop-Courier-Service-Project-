namespace Application.Abstractions;

/// <summary>
/// Addresses of the courier's own pages, for a link in a text or an email. Built from a configured format per tenant
/// subdomain, not from the current request: background work (the outbox) has none.
/// </summary>
public interface ITrackingLinks
{
    string Track(string trackingCode);

    /// <summary>The merchant's page for one payout, by its invoice number.</summary>
    string Invoice(string payoutNumber);

    /// <summary>
    /// Where the payment gateway sends the payer back after an online payment, by its transaction:
    /// <paramref name="outcome"/> is <c>success</c>, <c>fail</c> or <c>cancel</c>.
    /// </summary>
    string PaymentReturn(string transactionId, string outcome);

    /// <summary>Where the payment gateway posts its own notice of a payment, server to server.</summary>
    string PaymentNotice();

    /// <summary>Any page of the courier's site, by its path ("Merchant/Payments"), for a button in an email.</summary>
    string Page(string path);
}
