using System.Collections.Concurrent;
using System.Net;
using Microsoft.Data.SqlClient;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Application.Abstractions;

[assembly: AssemblyFixture(typeof(Integration.Tests.WebAppFactory))]

namespace Integration.Tests;

/// <summary>
/// The real web app against the test database: INTEGRATION_TEST_DB when set (CI, another developer's database),
/// otherwise ConnectionStrings:Database in testsettings.Local.json (git-ignored; OneDrop-Test on the shared server).
/// Development settings are used, so the demo seeder creates both couriers' logins, merchants and API keys on first
/// start (the launch courier comes from dbup, the rival courier from <see cref="InitializeAsync"/>).
/// </summary>
public sealed class WebAppFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    public const string Fashion = "od_odfashion001_DevOnlyKeyDoNotUseInProduction01";
    public const string Gadget = "od_odgadget0001_DevOnlyKeyDoNotUseInProduction02";
    public const string Beauty = "od_odbeauty0001_DevOnlyKeyDoNotUseInProduction03";
    public const string Rival = "od_rivalshop001_DevOnlyKeyDoNotUseInProduction09";

    public const string Password = "OneDrop#2026";

    /// <summary>Every webhook the app posts, in place of real HTTP: a test's shop gets an address of its own.</summary>
    public RecordingWebhooks Webhooks { get; } = new();

    public static string? ConnectionString { get; } = new ConfigurationBuilder()
        .SetBasePath(AppContext.BaseDirectory)
        .AddJsonFile("testsettings.json", optional: true)
        .AddJsonFile("testsettings.Local.json", optional: true)
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Database"] = Environment.GetEnvironmentVariable("INTEGRATION_TEST_DB")
        }.Where(setting => !string.IsNullOrWhiteSpace(setting.Value)))
        .Build()
        .GetConnectionString("Database");

    /// <summary>First line of every test: without a test database the test is reported as skipped.</summary>
    public static void RequireDatabase()
    {
        Assert.SkipWhen(
            string.IsNullOrWhiteSpace(ConnectionString),
            "No test database configured. Create testsettings.Local.json or set INTEGRATION_TEST_DB, and publish it with " +
            "./tools/db/publish.ps1 -Database OneDrop-Test.");
    }

    /// <summary>
    /// Makes sure the second courier exists, then starts the host once, before tests run in parallel. The launch data has
    /// one courier; the isolation tests need another, which lives in the test database only. WebApplicationFactory starts
    /// its host lazily without a lock, so parallel first calls each started a host and their demo seeders raced on an
    /// empty database.
    /// </summary>
    public async ValueTask InitializeAsync()
    {
        if (string.IsNullOrWhiteSpace(ConnectionString))
        {
            return;
        }

        await using (var connection = new SqlConnection(ConnectionString))
        {
            await connection.OpenAsync();
            await using var command = new SqlCommand(RivalCourier, connection);
            await command.ExecuteNonQueryAsync();
        }

        _ = Services;
    }

    /// <summary>The rival courier: one hub, zone and area, and a rate card of its own (other prices than the launch one).</summary>
    private const string RivalCourier = """
        IF NOT EXISTS (SELECT 1 FROM Platform.Tenant WHERE Slug = N'rival')
        BEGIN
            DECLARE @Rival BIGINT;
            INSERT INTO Platform.Tenant ([Name], Slug, TimeZone, CurrencyCode, SmsSenderName, SupportPhone, MaxDeliveryAttempts)
            VALUES (N'Rival Courier', N'rival', N'Asia/Dhaka', N'BDT', N'Rival', N'09610-999999', 2);
            SET @Rival = SCOPE_IDENTITY();
            INSERT INTO Network.Hub (TenantId, Code, [Name], [Address], Phone)
            VALUES (@Rival, N'RVL', N'Rival hub', N'Main Road, Rivalton', N'01799000000');
            INSERT INTO Network.Zone (TenantId, Code, [Name], HubId, City, IsSuburb)
            SELECT @Rival, N'RVL', N'Rivalton', Id, N'Rivalton', 0 FROM Network.Hub WHERE TenantId = @Rival;
            INSERT INTO Network.Area (TenantId, ZoneId, [Name])
            SELECT @Rival, Id, N'Rival Town' FROM Network.Zone WHERE TenantId = @Rival;
            INSERT INTO Pricing.DeliveryRate (TenantId, ServiceArea, IncludedWeightGrams, BaseCharge, ExtraKgCharge, CodChargePercent, ReturnCharge)
            VALUES (@Rival, 1, 500, 70, 20, 0, 20), (@Rival, 2, 500, 110, 20, 0, 40), (@Rival, 3, 500, 140, 25, 0, 70);
        END;
        -- The database scripts give every courier its two default delivery types, but the rival comes after them
        INSERT INTO Pricing.DeliveryOption (TenantId, Kind, Code, [Name], Icon, IsDefault, SortOrder)
        SELECT T.Id, O.Kind, O.Code, O.[Name], O.Icon, 1, 1
        FROM Platform.Tenant T
        CROSS JOIN (VALUES (1, N'regular', N'Regular', N'truck'), (2, N'general', N'General', N'package')) O (Kind, Code, [Name], Icon)
        WHERE T.Slug = N'rival' AND
              NOT EXISTS (SELECT 1 FROM Pricing.DeliveryOption D WHERE D.TenantId = T.Id AND D.Kind = O.Kind AND D.IsDefault = 1);
        """;

    public HttpClient ClientFor(string apiKey)
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", apiKey);

        return client;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        var settings = new Dictionary<string, string?>
        {
            ["ConnectionStrings:Database"] = ConnectionString,
            ["Seed:DemoData"] = "true",
            // No job server: jobs are run by the tests themselves, with a fake clock
            ["Jobs:Server"] = "false",
            ["Seed:DemoActivity"] = "false",
            ["Seed:Password"] = Password,
            // Never a real mail server in tests, whatever appsettings.Local.json holds: the fake keeps them in memory
            ["Email:Host"] = "",
            ["Email:From"] = "",
            // Never the real payment gateway either: without a store, Development uses the test gateway (/Dev/Pay)
            ["Sslcommerz:StoreId"] = "",
            ["Sslcommerz:StorePassword"] = "",
            // No Google Maps in tests: pages fall back to links, and short map links are opened by a stand-in
            ["GoogleMaps:ApiKey"] = "",
            // The courier is reached at onedrop.localhost here, whatever address a developer is tunnelling to
            ["Tenancy:RootDomain"] = "localhost",
            ["Tenancy:HomeCourierHost"] = ""
        };

        builder.UseEnvironment("Development");

        // Added after the web app's own sources (including its appsettings.Local.json, which points at the dev
        // database), so the test database always wins
        builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(settings));
        builder.ConfigureTestServices(services =>
        {
            services.AddSingleton<IWebhookSender>(Webhooks);
            services.AddSingleton<IMapLinks, ShortMapLinks>();
            services.AddTransient<IStartupFilter, ClientAddressFilter>();
        });
        foreach (var (key, value) in settings)
        {
            builder.UseSetting(key, value);
        }
    }
}

/// <summary>Stands in for Google's short links, so no test reaches the internet: one known link opens, any other does not.</summary>
public sealed class ShortMapLinks : IMapLinks
{
    public const string Link = "https://maps.app.goo.gl/TestPin";

    public Task<string?> ExpandAsync(string link, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(link == Link ? "https://www.google.com/maps/place/Test/@23.7509,90.3904,17z" : null);
    }
}

/// <summary>
/// Lets a test say which address its requests come from (header <see cref="Header"/>). Every test request otherwise
/// shares one address, and so one allowance of a limit counted per address, such as the phone sign-in's.
/// </summary>
public sealed class ClientAddressFilter : IStartupFilter
{
    public const string Header = "X-Test-Client";

    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
    {
        return app =>
        {
            app.Use((context, nextMiddleware) =>
            {
                if (IPAddress.TryParse(context.Request.Headers[Header], out var address))
                {
                    context.Connection.RemoteIpAddress = address;
                }

                return nextMiddleware(context);
            });
            next(app);
        };
    }
}

/// <summary>
/// Stands in for the shops' servers: keeps every webhook posted and answers 200, or 500 for an address a test has taken
/// down.
/// </summary>
public sealed class RecordingWebhooks : IWebhookSender
{
    private readonly ConcurrentQueue<WebhookRequest> posted = new();
    private readonly ConcurrentDictionary<string, bool> down = new();

    public IReadOnlyCollection<WebhookRequest> Posted => posted;

    public IReadOnlyList<WebhookRequest> To(string url)
    {
        return [.. posted.Where(request => request.Url.AbsoluteUri == url)];
    }

    public void Down(string url)
    {
        down[url] = true;
    }

    public void Up(string url)
    {
        down.TryRemove(url, out _);
    }

    public Task<WebhookResponse> PostAsync(WebhookRequest request, CancellationToken cancellationToken = default)
    {
        posted.Enqueue(request);

        return Task.FromResult(new WebhookResponse(down.ContainsKey(request.Url.AbsoluteUri) ? 500 : 200));
    }
}
