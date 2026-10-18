using System.Globalization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Application.Abstractions;
using Infrastructure.Connections;
using Infrastructure.Email;
using Infrastructure.Identity;
using Infrastructure.Jobs;
using Infrastructure.Maps;
using Infrastructure.MultiTenancy;
using Infrastructure.Payments;
using Infrastructure.Persistence;
using Infrastructure.Security;
using Infrastructure.Seeding;
using Infrastructure.Sms;
using Infrastructure.Webhooks;

namespace Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services)
    {
        services.AddSingleton(TimeProvider.System);
        services.AddMemoryCache();
        services.AddHttpContextAccessor();

        services.AddScoped<TenantContext>();
        services.AddScoped<ITenantContext>(provider => provider.GetRequiredService<TenantContext>());
        services.AddSingleton<ITenantCatalog, TenantCatalog>();
        services.AddScoped<ICurrentUser, CurrentUser>();
        services.AddScoped<ITrackingLinks, TrackingLinks>();
        services.AddScoped<IUserAccounts, UserAccounts>();

        services.AddScoped<TenantSaveInterceptor>();
        services.AddScoped<TenantSessionInterceptor>();
        // Read from the final configuration when the context is built, not at registration, so every source -
        // appsettings.Local.json, environment variables, a test host's overrides - is already applied
        services.AddDbContext<AppDbContext>((provider, options) => options
            .UseSqlServer(provider.GetRequiredService<IConfiguration>().GetConnectionString("Database")
                ?? throw new InvalidOperationException(
                    "ConnectionStrings:Database is not configured. Put it in src/Web/appsettings.Local.json."))
            .AddInterceptors(
                provider.GetRequiredService<TenantSaveInterceptor>(),
                provider.GetRequiredService<TenantSessionInterceptor>())
            .ConfigureWarnings(warnings => warnings.Ignore(
                // Identity's claim/role/login rows hang off the filtered user; they are only ever read through it
                CoreEventId.PossibleIncorrectRequiredNavigationWithQueryFilterInteractionWarning,
                // The outbox save runs in a transaction that is rolled back whole when a save fails, so it never
                // needs the savepoints MARS turns off
                SqlServerEventId.SavepointsDisabledBecauseOfMARS)));
        services.AddScoped<IAppDbContext>(provider => provider.GetRequiredService<AppDbContext>());

        services.AddSingleton<SmsLog>();
        services.AddSingleton<ISmsSender, FakeSmsSender>();
        services.AddSingleton<FakePayoutLog>();
        services.AddSingleton<IPayoutGateway, FakePayoutGateway>();
        services.AddScoped<IJobSchedule, HangfireJobSchedule>();

        // The key ring signs the sign-in cookies and seals the secrets a courier keeps, so it lives in the database:
        // a restart, a redeploy or a second server reads the same keys. The name keeps them the app's wherever it runs
        services.AddDataProtection().SetApplicationName("Courier");
        services.AddOptions<KeyManagementOptions>()
            .Configure<IConfiguration>((options, configuration) => options.XmlRepository = new SqlKeyRepository(
                configuration.GetConnectionString("Database")
                    ?? throw new InvalidOperationException("ConnectionStrings:Database is not configured.")));
        services.AddSingleton<ISecretBox, SecretBox>();

        // Each courier's mail server and SSLCommerz store: its own from /Admin/Connections, else the server's
        // configuration (Email:*, Sslcommerz:*, in a git-ignored Local.json); with neither, the fakes in Development
        services.AddScoped<ConnectionSettings>();
        services.AddScoped<IConnectionSettings>(provider => provider.GetRequiredService<ConnectionSettings>());
        services.AddSingleton<FakePaymentLog>();
        services.AddHttpClient(CourierPaymentGateway.HttpClientName, client => client.Timeout = TimeSpan.FromSeconds(30));
        services.AddScoped<IPaymentGateway, CourierPaymentGateway>();
        services.AddSingleton<EmailLog>();
        services.AddScoped<IEmailSender, CourierEmailSender>();

        services
            .AddHttpClient<IWebhookSender, HttpWebhookSender>((provider, client) => client.Timeout = TimeSpan.Parse(
                provider.GetRequiredService<IConfiguration>()["Webhooks:Timeout"]
                    ?? throw new InvalidOperationException("Webhooks:Timeout is not set."),
                CultureInfo.InvariantCulture))
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false });

        // Google Maps' short links are opened one redirect at a time, so only Google's own short hosts are ever asked
        services
            .AddHttpClient<IMapLinks, GoogleMapLinks>(client =>
            {
                client.Timeout = TimeSpan.FromSeconds(5);
                client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0");
            })
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false });

        services.AddSingleton<DemoDataSeeder>();
        services.AddSingleton<AdminSetup>();

        return services;
    }

    /// <summary>Identity stores and options. The web host adds the cookie scheme on top.</summary>
    public static IdentityBuilder AddAppIdentity(this IServiceCollection services)
    {
        // A stopped moderator or staff member, or one whose role changed, is caught within a minute rather than Identity's
        // default thirty
        services.Configure<SecurityStampValidatorOptions>(options => options.ValidationInterval = TimeSpan.FromMinutes(1));

        return services
            .AddIdentity<AppUser, AppRole>(options =>
            {
                options.User.RequireUniqueEmail = false;
                options.Password.RequiredLength = 8;
                options.Password.RequireNonAlphanumeric = false;
                options.SignIn.RequireConfirmedAccount = false;
            })
            .AddEntityFrameworkStores<AppDbContext>()
            .AddClaimsPrincipalFactory<AppClaimsFactory>()
            .AddDefaultTokenProviders();
    }
}
