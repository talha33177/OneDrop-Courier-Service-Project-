using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Application.Abstractions;
using Domain.Payments;

namespace Infrastructure.Payments;

/// <summary>
/// The SSLCommerz store to take payments with, from configuration (<c>Sslcommerz:*</c>). The store password belongs in
/// a git-ignored <c>*.Local.json</c>, never in the repository. <see cref="BaseUrl"/> is the sandbox or the live
/// gateway. Without a store the app uses the fake gateway in Development and offers no online payments elsewhere.
/// </summary>
public sealed class SslcommerzOptions
{
    public string? StoreId { get; set; }

    public string? StorePassword { get; set; }

    /// <summary>https://sandbox.sslcommerz.com/ to test, https://securepay.sslcommerz.com/ for real money.</summary>
    public string? BaseUrl { get; set; }

    /// <summary>The smallest and largest payment the gateway takes (SSLCommerz: ৳10 to ৳500,000).</summary>
    public decimal MinimumAmount { get; set; }

    public decimal MaximumAmount { get; set; }

    /// <summary>The payer's country, which the payment page asks for.</summary>
    public string? Country { get; set; }

    public bool Configured => !string.IsNullOrWhiteSpace(StoreId) && !string.IsNullOrWhiteSpace(StorePassword);

    public PaymentLimits Limits => new(MinimumAmount, MaximumAmount);
}

/// <summary>
/// SSLCommerz (API v4). A payment is opened with the session API, which answers with the payment page to send the payer
/// to; the payer comes back to our success, fail or cancel address with the gateway's validation id, and the payment is
/// believed only once the validation API, asked by us with the store's password, confirms it. The transaction query
/// answers for a payer who never came back.
/// </summary>
public class SslcommerzGateway(HttpClient http, SslcommerzOptions options, ILogger<SslcommerzGateway> logger) : IPaymentGateway
{
    public string Name => "SSLCommerz";

    public bool IsAvailable => true;

    public PaymentLimits Limits => options.Limits;

    public async Task<string> StartAsync(PaymentSession session, CancellationToken cancellationToken = default)
    {
        var fields = new Dictionary<string, string>
        {
            ["store_id"] = options.StoreId!,
            ["store_passwd"] = options.StorePassword!,
            ["total_amount"] = session.Amount.ToString("0.00", CultureInfo.InvariantCulture),
            ["currency"] = session.Currency,
            ["tran_id"] = session.TransactionId,
            ["success_url"] = session.SuccessUrl,
            ["fail_url"] = session.FailUrl,
            ["cancel_url"] = session.CancelUrl,
            ["ipn_url"] = session.NoticeUrl,
            ["cus_name"] = Clip(session.Payer.Name, 50),
            ["cus_email"] = Clip(session.Payer.Email, 50),
            ["cus_phone"] = Clip(session.Payer.Phone, 20),
            ["cus_add1"] = Clip(session.Payer.Address, 50),
            ["cus_city"] = Clip(session.Payer.City, 50),
            ["cus_country"] = options.Country ?? "",
            ["shipping_method"] = "NO",
            ["num_of_item"] = "1",
            ["product_name"] = Clip(session.Description, 255),
            ["product_category"] = "Courier charges",
            ["product_profile"] = "non-physical-goods"
        };

        using var answer = await SendAsync(
            () => http.PostAsync("gwprocess/v4/api.php", new FormUrlEncodedContent(fields), cancellationToken),
            cancellationToken);
        var root = answer.RootElement;
        if (!string.Equals(Text(root, "status"), "SUCCESS", StringComparison.OrdinalIgnoreCase)
            || Text(root, "GatewayPageURL") is not { Length: > 0 } page)
        {
            var reason = Text(root, "failedreason");
            logger.LogWarning("SSLCommerz refused session {TransactionId}: {Reason}", session.TransactionId, reason);

            throw new PaymentGatewayException(string.IsNullOrWhiteSpace(reason) ? "It gave no reason." : reason.TrimEnd('.') + ".");
        }

        return page;
    }

    public async Task<GatewayReceipt?> ValidateAsync(string validationId, CancellationToken cancellationToken = default)
    {
        using var answer = await SendAsync(
            () => http.GetAsync(
                "validator/api/validationserverAPI.php" + Query(("val_id", validationId), ("format", "json"), ("v", "1")),
                cancellationToken),
            cancellationToken);

        return Receipt(answer.RootElement, Text(answer.RootElement, "tran_id"));
    }

    public async Task<GatewayLookup> LookUpAsync(string transactionId, CancellationToken cancellationToken = default)
    {
        using var answer = await SendAsync(
            () => http.GetAsync(
                "validator/api/merchantTransIDvalidationAPI.php" + Query(("tran_id", transactionId), ("format", "json")),
                cancellationToken),
            cancellationToken);
        var root = answer.RootElement;
        if (Text(root, "APIConnect") is { } connect && !string.Equals(connect, "DONE", StringComparison.OrdinalIgnoreCase))
        {
            throw new PaymentGatewayException($"The transaction query was refused ({connect}).");
        }

        var found = root.TryGetProperty("element", out var element) && element.ValueKind == JsonValueKind.Array
            ? element.EnumerateArray().ToList()
            : [];
        if (found.Select(item => Receipt(item, transactionId)).FirstOrDefault(receipt => receipt is not null) is { } paid)
        {
            return new GatewayLookup(GatewayState.Paid, paid);
        }

        var statuses = found.Select(item => Text(item, "status")?.ToUpperInvariant()).ToList();
        if (statuses.Count == 0 || statuses.Any(status => status is not ("FAILED" or "CANCELLED" or "EXPIRED")))
        {
            return new GatewayLookup(GatewayState.Pending);
        }

        return new GatewayLookup(
            GatewayState.Failed,
            Reason: statuses.Last() switch
            {
                "CANCELLED" => "It was cancelled on the payment page.",
                "EXPIRED" => "The payment page expired before it was paid.",
                _ => "The payment failed at the gateway."
            });
    }

    public async Task<RefundAnswer> RefundAsync(
        string bankTransactionId,
        decimal amount,
        string reference,
        string remarks,
        CancellationToken cancellationToken = default)
    {
        using var answer = await SendAsync(
            () => http.GetAsync(
                "validator/api/merchantTransIDvalidationAPI.php" + Query(
                    ("bank_tran_id", bankTransactionId),
                    ("refund_amount", amount.ToString("0.00", CultureInfo.InvariantCulture)),
                    ("refund_remarks", Clip(remarks, 255)),
                    ("refe_id", reference),
                    ("format", "json")),
                cancellationToken),
            cancellationToken);

        return Refund(answer.RootElement);
    }

    public async Task<RefundAnswer> RefundStatusAsync(string refundReference, CancellationToken cancellationToken = default)
    {
        using var answer = await SendAsync(
            () => http.GetAsync(
                "validator/api/merchantTransIDvalidationAPI.php" + Query(("refund_ref_id", refundReference), ("format", "json")),
                cancellationToken),
            cancellationToken);

        return Refund(answer.RootElement) with { Reference = refundReference };
    }

    /// <summary>
    /// The refund API's answer: "success" or "refunded" when the money went back, "processing" while the bank is still
    /// on it, anything else refused, with its "errorReason".
    /// </summary>
    private static RefundAnswer Refund(JsonElement root)
    {
        if (Text(root, "APIConnect") is { } connect && !string.Equals(connect, "DONE", StringComparison.OrdinalIgnoreCase))
        {
            throw new PaymentGatewayException($"The refund request was refused ({connect}).");
        }

        var reference = Text(root, "refund_ref_id");

        return Text(root, "status")?.ToLowerInvariant() switch
        {
            "success" or "refunded" => new RefundAnswer(RefundState.Done, reference),
            "processing" => new RefundAnswer(RefundState.Processing, reference),
            _ => new RefundAnswer(RefundState.Refused, reference, Text(root, "errorReason") ?? "It gave no reason.")
        };
    }

    /// <summary>A paid answer (VALID, or VALIDATED when we asked before) as a receipt; null for anything else.</summary>
    private static GatewayReceipt? Receipt(JsonElement answer, string? transactionId)
    {
        if (Text(answer, "status")?.ToUpperInvariant() is not ("VALID" or "VALIDATED")
            || string.IsNullOrEmpty(transactionId)
            || Text(answer, "val_id") is not { Length: > 0 } validationId)
        {
            return null;
        }

        // The currency and amount the payment was asked in; "amount" is the same converted to taka
        var currency = Text(answer, "currency_type") ?? Text(answer, "currency") ?? "";
        var amount = Money(answer, "currency_amount") ?? Money(answer, "amount") ?? 0;

        return new GatewayReceipt(
            transactionId,
            validationId,
            amount,
            currency,
            Money(answer, "store_amount"),
            Text(answer, "card_type"),
            Text(answer, "bank_tran_id"),
            Text(answer, "risk_level") != "0",
            Text(answer, "risk_title"));
    }

    private async Task<JsonDocument> SendAsync(Func<Task<HttpResponseMessage>> request, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await request();
            if (!response.IsSuccessStatusCode)
            {
                throw new PaymentGatewayException($"SSLCommerz answered {(int)response.StatusCode}.");
            }

            return await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException
            || (exception is TaskCanceledException && !cancellationToken.IsCancellationRequested))
        {
            throw new PaymentGatewayException("SSLCommerz could not be reached.", exception);
        }
    }

    private string Query(params (string Name, string Value)[] fields)
    {
        return "?" + string.Join(
            "&",
            fields
                .Append((Name: "store_id", Value: options.StoreId!))
                .Append((Name: "store_passwd", Value: options.StorePassword!))
                .Select(field => $"{field.Name}={Uri.EscapeDataString(field.Value)}"));
    }

    /// <summary>A field the gateway may send as a string or a number, as text; null when missing or empty.</summary>
    private static string? Text(JsonElement answer, string name)
    {
        if (answer.ValueKind != JsonValueKind.Object || !answer.TryGetProperty(name, out var value))
        {
            return null;
        }

        var text = value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            _ => null
        };

        return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
    }

    private static decimal? Money(JsonElement answer, string name)
    {
        return decimal.TryParse(Text(answer, name), NumberStyles.Number, CultureInfo.InvariantCulture, out var amount) ? amount : null;
    }

    private static string Clip(string value, int length)
    {
        var trimmed = value.Trim();

        return trimmed.Length > length ? trimmed[..length] : trimmed;
    }
}

/// <summary>No gateway is set up and this is not Development: online payments are not offered.</summary>
public class NoPaymentGateway(SslcommerzOptions options) : IPaymentGateway
{
    public string Name => "SSLCommerz";

    public bool IsAvailable => false;

    public PaymentLimits Limits => options.Limits;

    public Task<string> StartAsync(PaymentSession session, CancellationToken cancellationToken = default)
    {
        throw new PaymentGatewayException("Online payments are not set up.");
    }

    public Task<GatewayReceipt?> ValidateAsync(string validationId, CancellationToken cancellationToken = default)
    {
        throw new PaymentGatewayException("Online payments are not set up.");
    }

    public Task<GatewayLookup> LookUpAsync(string transactionId, CancellationToken cancellationToken = default)
    {
        throw new PaymentGatewayException("Online payments are not set up.");
    }

    public Task<RefundAnswer> RefundAsync(
        string bankTransactionId,
        decimal amount,
        string reference,
        string remarks,
        CancellationToken cancellationToken = default)
    {
        throw new PaymentGatewayException("Online payments are not set up.");
    }

    public Task<RefundAnswer> RefundStatusAsync(string refundReference, CancellationToken cancellationToken = default)
    {
        throw new PaymentGatewayException("Online payments are not set up.");
    }
}
