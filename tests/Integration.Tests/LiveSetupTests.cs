using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
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
    public async Task A_home_courier_host_serves_the_courier_on_an_address_of_its_own()
    {
        WebAppFactory.RequireDatabase();
        await using var live = Factory.WithWebHostBuilder(builder =>
            builder.UseSetting("Tenancy:HomeCourierHost", "courier.example"));
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
