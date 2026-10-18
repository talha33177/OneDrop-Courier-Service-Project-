using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Application.Abstractions;
using Infrastructure.Email;
using Infrastructure.Payments;

namespace Infrastructure.Connections;

/// <summary>
/// The mail server and SSLCommerz store the current courier's emails and payments go through: its own, set by its
/// super admin on <c>/Admin/Connections</c>, else the server's configuration (<c>Email:*</c>, <c>Sslcommerz:*</c>).
/// Read when first asked, not when built, because a job sets its courier after its services are made. Each courier's
/// own settings are cached for a few minutes and dropped by <see cref="Forget"/> when they change.
/// </summary>
public class ConnectionSettings(
    IAppDbContext db,
    ITenantContext tenantContext,
    ISecretBox secrets,
    IConfiguration configuration,
    IMemoryCache cache,
    IHostEnvironment environment,
    ILogger<ConnectionSettings> logger) : IConnectionSettings
{
    private static readonly TimeSpan Keep = TimeSpan.FromMinutes(10);

    public bool ServerHasMail => ServerMail().Configured;

    public bool ServerHasStore => ServerStore().Configured;

    public bool EmailsArrive => Mail().Configured || environment.IsDevelopment();

    public EmailOptions Mail()
    {
        return Own()?.Mail ?? ServerMail();
    }

    public SslcommerzOptions Store()
    {
        return Own()?.Store ?? ServerStore();
    }

    public void Forget()
    {
        if (tenantContext.TenantId is { } tenantId)
        {
            cache.Remove(Key(tenantId));
        }
    }

    private Saved? Own()
    {
        if (tenantContext.TenantId is not { } tenantId)
        {
            return null;
        }

        return cache.GetOrCreate(Key(tenantId), entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = Keep;
            var server = db.MailServers.AsNoTracking().SingleOrDefault();
            var store = db.GatewayStores.AsNoTracking().SingleOrDefault();
            var mailPassword = server is null ? null : secrets.Open(server.Password);
            var storePassword = store is null ? null : secrets.Open(store.StorePassword);
            if ((server is not null && mailPassword is null) || (store is not null && storePassword is null))
            {
                // The key ring it was sealed with is gone: the super admin has to enter it again
                logger.LogWarning("A saved password for tenant {TenantId} can no longer be read; the server's own settings are used", tenantId);
            }

            var file = ServerStore();

            return new Saved(
                server is null || mailPassword is null
                    ? null
                    : new EmailOptions
                    {
                        Host = server.Host,
                        Port = server.Port,
                        User = server.UserName,
                        Password = mailPassword,
                        From = server.FromAddress,
                        FromName = server.FromName
                    },
                store is null || storePassword is null
                    ? null
                    : new SslcommerzOptions
                    {
                        StoreId = store.StoreId,
                        StorePassword = storePassword,
                        BaseUrl = store.IsLive ? configuration["Sslcommerz:LiveUrl"] : configuration["Sslcommerz:SandboxUrl"],
                        MinimumAmount = file.MinimumAmount,
                        MaximumAmount = file.MaximumAmount,
                        Country = file.Country
                    });
        });
    }

    private EmailOptions ServerMail()
    {
        var options = new EmailOptions();
        configuration.GetSection("Email").Bind(options);

        return options;
    }

    private SslcommerzOptions ServerStore()
    {
        var options = new SslcommerzOptions();
        configuration.GetSection("Sslcommerz").Bind(options);

        return options;
    }

    private static string Key(long tenantId)
    {
        return $"connections-{tenantId}";
    }

    private sealed record Saved(EmailOptions? Mail, SslcommerzOptions? Store);
}
