using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Infrastructure.Seeding;

namespace Integration.Tests;

/// <summary>
/// What a live site starts with: the courier's first admin from the Setup settings, and the courier served on one
/// address of its own instead of a subdomain.
/// </summary>
public class LiveSetupTests(WebAppFactory factory) : AppTests(factory)
{
    [Fact]
    public async Task The_first_admin_is_made_once_and_never_changed_afterwards()
    {
        WebAppFactory.RequireDatabase();
        var setup = Factory.Services.GetRequiredService<AdminSetup>();
        var email = $"{Guid.NewGuid():N}@admin.example";

        Assert.True(await setup.EnsureCourierAdminAsync("onedrop", email, "Live admin", "Chosen#2026", Cancel));
        // Later starts, with the password setting changed or emptied: the admin keeps the password it has
        Assert.False(await setup.EnsureCourierAdminAsync("onedrop", email, "Live admin", "Another#2026", Cancel));
        Assert.False(await setup.EnsureCourierAdminAsync("onedrop", email, "Live admin", null, Cancel));

        var admin = Visit("onedrop", await SignInCookieAsync("onedrop", email, "Chosen#2026"));
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync("/Admin")).StatusCode);
        var changed = await Visit("onedrop").PostFormAsync(
            "/Account/Login",
            "/Account/Login",
            ("Input.Email", email),
            ("Input.Password", "Another#2026"));
        Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
        var elsewhere = await Visit("rival").PostFormAsync(
            "/Account/Login",
            "/Account/Login",
            ("Input.Email", email),
            ("Input.Password", "Chosen#2026"));
        Assert.Equal(HttpStatusCode.OK, elsewhere.StatusCode);

        // A password the sign-in rules refuse, or a courier that is not there, stops the start with the reason
        var weak = await Assert.ThrowsAsync<InvalidOperationException>(() => setup.EnsureCourierAdminAsync(
            "onedrop",
            $"{Guid.NewGuid():N}@admin.example",
            "Live admin",
            "short",
            Cancel));
        Assert.Contains("Setup:AdminPassword", weak.Message);
        await Assert.ThrowsAsync<InvalidOperationException>(() => setup.EnsureCourierAdminAsync(
            "nowhere",
            email,
            "Live admin",
            "Chosen#2026",
            Cancel));
    }

    [Fact]
    public async Task A_live_start_with_no_admin_named_makes_the_demo_admin_and_a_named_one_never_falls_back_to_it()
    {
        WebAppFactory.RequireDatabase();
        var demo = $"{Guid.NewGuid():N}@demo.example";

        // Production, nobody named: the default admin is made, so a fresh server can be signed in to
        await using (var live = Factory.WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Production");
            builder.UseSetting("Setup:AdminEmail", "");
            builder.UseSetting("Setup:DefaultAdminEmail", demo);
            builder.UseSetting("Setup:DefaultAdminPassword", "Default#2026");
        }))
        {
            _ = live.Services;
        }

        var admin = Visit("onedrop", await SignInCookieAsync("onedrop", demo, "Default#2026"));
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync("/Admin")).StatusCode);

        // An admin named without a password stops the start with the reason, and the default is not used in its place
        var other = $"{Guid.NewGuid():N}@demo.example";
        await using var named = Factory.WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Production");
            builder.UseSetting("Setup:AdminEmail", $"{Guid.NewGuid():N}@named.example");
            builder.UseSetting("Setup:AdminPassword", "");
            builder.UseSetting("Setup:DefaultAdminEmail", other);
            builder.UseSetting("Setup:DefaultAdminPassword", "Default#2026");
        });
        Assert.ThrowsAny<Exception>(() => named.Services);
        var refused = await Visit("onedrop").PostFormAsync(
            "/Account/Login",
            "/Account/Login",
            ("Input.Email", other),
            ("Input.Password", "Default#2026"));
        Assert.Equal(HttpStatusCode.OK, refused.StatusCode);
        Assert.Contains("is not right for", await refused.Content.ReadAsStringAsync(Cancel));
    }

    [Fact]
    public async Task The_demo_admin_script_gives_a_locked_out_admin_the_demo_password_and_makes_no_login()
    {
        WebAppFactory.RequireDatabase();
        var setup = Factory.Services.GetRequiredService<AdminSetup>();
        var email = $"{Guid.NewGuid():N}@admin.example";
        Assert.True(await setup.EnsureCourierAdminAsync("onedrop", email, "Live admin", "Chosen#2026", Cancel));

        // Locked out, as the old limit on wrong tries left it after guesses at a password nobody had
        await Sql(
            "UPDATE [Identity].[User] SET LockoutEnd = DATEADD(HOUR, 1, SYSDATETIMEOFFSET()), AccessFailedCount = 4 " +
            $"WHERE NormalizedUserName = N'{email.ToUpperInvariant()}'");
        var before = await Visit("onedrop").PostFormAsync(
            "/Account/Login",
            "/Account/Login",
            ("Input.Email", email),
            ("Input.Password", WebAppFactory.Password));
        Assert.Contains("has been stopped", await before.Content.ReadAsStringAsync(Cancel));

        // The real script, aimed at this login instead of admin@onedrop.test
        var script = await ScriptAsync("012_DemoAdminPassword.sql");
        Assert.Contains("N'ADMIN@ONEDROP.TEST'", script);
        script = script.Replace("ADMIN@ONEDROP.TEST", email.ToUpperInvariant());
        await Sql(script);

        var admin = Visit("onedrop", await SignInCookieAsync("onedrop", email, WebAppFactory.Password));
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync("/Admin")).StatusCode);
        var old = await Visit("onedrop").PostFormAsync(
            "/Account/Login",
            "/Account/Login",
            ("Input.Email", email),
            ("Input.Password", "Chosen#2026"));
        Assert.Contains("is not right for", await old.Content.ReadAsStringAsync(Cancel));

        // A login that is not there is not made
        var absent = $"{Guid.NewGuid():N}@absent.example";
        await Sql(script.Replace(email.ToUpperInvariant(), absent.ToUpperInvariant()));
        await using var connection = new SqlConnection(WebAppFactory.ConnectionString);
        await connection.OpenAsync(Cancel);
        await using var count = new SqlCommand(
            $"SELECT COUNT(*) FROM [Identity].[User] WHERE NormalizedEmail = N'{absent.ToUpperInvariant()}'",
            connection);
        Assert.Equal(0, (int)(await count.ExecuteScalarAsync(Cancel))!);
    }

    [Fact]
    public async Task A_home_courier_host_serves_the_courier_on_an_address_of_its_own()
    {
        WebAppFactory.RequireDatabase();
        // Through the app's configuration, like the factory's own empty value that it has to come after
        await using var live = Factory.WithWebHostBuilder(builder =>
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
                new Dictionary<string, string?> { ["Tenancy:HomeCourierHost"] = "courier.example" })));
        var client = live.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("http://courier.example"),
            AllowAutoRedirect = false
        });

        var front = await client.GetAsync("/", Cancel);
        Assert.Equal(HttpStatusCode.OK, front.StatusCode);
        Assert.Contains("OneDrop Courier", await front.Content.ReadAsStringAsync(Cancel));
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/Coverage", Cancel)).StatusCode);

        // The bare domain sends visitors to that address, and the courier's subdomain still answers
        var bare = await client.GetAsync("http://localhost/", Cancel);
        Assert.Equal(HttpStatusCode.Redirect, bare.StatusCode);
        Assert.Equal("http://courier.example/", bare.Headers.Location!.OriginalString);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("http://onedrop.localhost/", Cancel)).StatusCode);
    }

    [Fact]
    public async Task A_mistyped_address_is_a_page_for_people_and_problem_details_for_the_api()
    {
        WebAppFactory.RequireDatabase();

        var page = await Visit("onedrop").GetAsync("/no-such-page");
        Assert.Equal(HttpStatusCode.NotFound, page.StatusCode);
        Assert.Equal("text/html", page.Content.Headers.ContentType?.MediaType);
        Assert.Contains("We could not find that page", await page.Content.ReadAsStringAsync(Cancel));

        var api = await Factory.ClientFor(WebAppFactory.Fashion).GetAsync("/api/v1/no-such-thing", Cancel);
        Assert.Equal(HttpStatusCode.NotFound, api.StatusCode);
        Assert.Equal("application/problem+json", api.Content.Headers.ContentType?.MediaType);
    }
}
