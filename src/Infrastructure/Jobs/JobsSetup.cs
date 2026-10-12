using System.Globalization;
using Hangfire;
using Hangfire.SqlServer;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Application.Delivery.Pickups;
using Application.Notifications.SendEmails;
using Application.Notifications.SendOutbox;
using Application.Notifications.SendWebhooks;
using Application.Payments.OnlinePayments;
using Application.Payments.RunPayouts;

namespace Infrastructure.Jobs;

/// <summary>
/// Hangfire on the application database. Hangfire owns its tables (schema <c>HangFire</c>, created and upgraded by
/// Hangfire on first use); the SQL project leaves them alone because the publish never drops objects it does not
/// know. The job server and the outbox dispatcher run only where <c>Jobs:Server</c> is true, so the integration tests
/// start neither.
/// </summary>
public static class JobsSetup
{
    public static IServiceCollection AddJobs(this IServiceCollection services, bool runServer)
    {
        services.AddSingleton(new TenantJobRegistry(
            typeof(SendOutboxJob),
            typeof(SendWebhooksJob),
            typeof(SendEmailsJob),
            typeof(PayoutsJob),
            typeof(CheckOnlinePaymentsJob),
            typeof(PickupsJob)));
        services.AddScoped<TenantJobRunner>();
        // The connection string is read when Hangfire first opens a connection, from the final configuration
        services.AddHangfire((provider, configuration) => configuration
            .SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
            .UseSimpleAssemblyNameTypeSerializer()
            .UseRecommendedSerializerSettings()
            .UseSqlServerStorage(
                () => new SqlConnection(provider.GetRequiredService<IConfiguration>().GetConnectionString("Database")),
                new SqlServerStorageOptions { PrepareSchemaIfNecessary = true }));
        if (runServer)
        {
            services.AddHangfireServer();
            services.AddHostedService(provider => ActivatorUtilities.CreateInstance<OutboxDispatcher>(
                provider,
                TimeSpan.Parse(
                    provider.GetRequiredService<IConfiguration>()["Jobs:OutboxInterval"]
                        ?? throw new InvalidOperationException("Jobs:OutboxInterval is not set."),
                    CultureInfo.InvariantCulture)));
        }

        return services;
    }

    /// <summary>Adds or updates the recurring jobs. Their schedules are cron expressions from <c>Jobs:*</c>.</summary>
    public static void ScheduleJobs(this IServiceProvider services, IConfiguration configuration)
    {
        var recurring = services.GetRequiredService<IRecurringJobManager>();
        recurring.AddOrUpdate<TenantJobRunner>(
            PayoutsJob.RecurringId,
            runner => runner.EnqueueForEveryTenantAsync(nameof(PayoutsJob), CancellationToken.None),
            configuration["Jobs:Payouts"] ?? throw new InvalidOperationException("Jobs:Payouts is not set."));
        recurring.AddOrUpdate<TenantJobRunner>(
            OnlinePaymentsHandler.RecurringId,
            runner => runner.EnqueueForEveryTenantAsync(nameof(CheckOnlinePaymentsJob), CancellationToken.None),
            configuration["Jobs:OnlinePayments"] ?? throw new InvalidOperationException("Jobs:OnlinePayments is not set."));
        recurring.AddOrUpdate<TenantJobRunner>(
            PickupSchedule.RecurringId,
            runner => runner.EnqueueForEveryTenantAsync(nameof(PickupsJob), CancellationToken.None),
            configuration["Jobs:Pickups"] ?? throw new InvalidOperationException("Jobs:Pickups is not set."));
    }
}
