using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Application.Abstractions;
using Domain.Payments;
using Infrastructure.Email;
using Infrastructure.Payments;

namespace Infrastructure.Connections;

/// <summary>
/// How an email reaches a mail server. The real one talks SMTP; the integration tests plug in one that only records,
/// so no test can ever send through a mail server a courier saved.
/// </summary>
public interface IMailTransports
{
    IEmailSender For(EmailOptions options);
}

/// <summary>The real transport: SMTP with STARTTLS to the courier's mail server.</summary>
public sealed class SmtpMailTransports(EmailLog log, TimeProvider time, ILoggerFactory loggers) : IMailTransports
{
    public IEmailSender For(EmailOptions options)
    {
        return new SmtpEmailSender(options, log, time, loggers.CreateLogger<SmtpEmailSender>());
    }
}

/// <summary>
/// Sends through the courier's mail server (<see cref="ConnectionSettings"/>), chosen at each send: the real server when
/// one is set, else the fake that keeps the email for <c>/Dev/Emails</c>.
/// </summary>
public class CourierEmailSender(ConnectionSettings connections, IMailTransports transports, EmailLog log, TimeProvider time, ILoggerFactory loggers) : IEmailSender
{
    public Task SendAsync(EmailMessage message, CancellationToken cancellationToken = default)
    {
        var options = connections.Mail();
        IEmailSender sender = options.Configured
            ? transports.For(options)
            : new FakeEmailSender(log, time, loggers.CreateLogger<FakeEmailSender>());

        return sender.SendAsync(message, cancellationToken);
    }
}

/// <summary>
/// The courier's SSLCommerz store (<see cref="ConnectionSettings"/>), chosen when first used: the real gateway when a
/// store is set, the fake in Development, and none elsewhere, so a fake can never take "payments" for real.
/// </summary>
public class CourierPaymentGateway(
    ConnectionSettings connections,
    IHttpClientFactory clients,
    IHostEnvironment environment,
    FakePaymentLog fakeLog,
    TimeProvider time,
    ILoggerFactory loggers) : IPaymentGateway
{
    public const string HttpClientName = "Sslcommerz";

    private IPaymentGateway? gateway;

    private IPaymentGateway Gateway => gateway ??= Choose();

    public string Name => Gateway.Name;

    public bool IsAvailable => Gateway.IsAvailable;

    public PaymentLimits Limits => Gateway.Limits;

    public Task<string> StartAsync(PaymentSession session, CancellationToken cancellationToken = default)
    {
        return Gateway.StartAsync(session, cancellationToken);
    }

    public Task<GatewayReceipt?> ValidateAsync(string validationId, CancellationToken cancellationToken = default)
    {
        return Gateway.ValidateAsync(validationId, cancellationToken);
    }

    public Task<GatewayLookup> LookUpAsync(string transactionId, CancellationToken cancellationToken = default)
    {
        return Gateway.LookUpAsync(transactionId, cancellationToken);
    }

    public Task<RefundAnswer> RefundAsync(
        string bankTransactionId,
        decimal amount,
        string reference,
        string remarks,
        CancellationToken cancellationToken = default)
    {
        return Gateway.RefundAsync(bankTransactionId, amount, reference, remarks, cancellationToken);
    }

    public Task<RefundAnswer> RefundStatusAsync(string refundReference, CancellationToken cancellationToken = default)
    {
        return Gateway.RefundStatusAsync(refundReference, cancellationToken);
    }

    private IPaymentGateway Choose()
    {
        var options = connections.Store();
        if (options.Configured)
        {
            var http = clients.CreateClient(HttpClientName);
            http.BaseAddress = new Uri(options.BaseUrl ?? throw new InvalidOperationException("The SSLCommerz gateway's address is not set."));

            return new SslcommerzGateway(http, options, loggers.CreateLogger<SslcommerzGateway>());
        }

        return environment.IsDevelopment()
            ? new FakePaymentGateway(fakeLog, options, time, loggers.CreateLogger<FakePaymentGateway>())
            : new NoPaymentGateway(options);
    }
}
