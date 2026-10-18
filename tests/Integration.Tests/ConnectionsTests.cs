using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Infrastructure.Connections;

namespace Integration.Tests;

/// <summary>
/// A super admin keeps the courier's own mail server and SSLCommerz store on /Admin/Connections. It works on the rival
/// courier and removes both again, so every other test keeps the server's settings (the fakes). The mail server it
/// saves does not exist, so nothing is ever sent, and no payment is started while the store is set.
/// </summary>
public class ConnectionsTests(WebAppFactory factory) : AppTests(factory)
{
    [Fact]
    public async Task A_super_admin_keeps_the_courier_s_mail_server_and_store_sealed_and_for_that_courier_only()
    {
        WebAppFactory.RequireDatabase();
        var admin = await SignInAsync("rival", "admin@rival.test");
        var mailPassword = $"mail-{Guid.NewGuid():N}";
        var storePassword = $"store-{Guid.NewGuid():N}";

        try
        {
            var saved = await admin.SubmitAsync(
                "/Admin/Connections",
                "/Admin/Connections?handler=Mail",
                ("host", "smtp.invalid"),
                ("port", "587"),
                ("userName", ""),
                ("password", mailPassword),
                ("fromAddress", "hello@rival.test"),
                ("fromName", "Rival Courier"));
            Assert.Contains("Mail server saved", saved);
            Assert.DoesNotContain(mailPassword, saved);

            saved = await admin.SubmitAsync(
                "/Admin/Connections",
                "/Admin/Connections?handler=Store",
                ("storeId", "rivalstore01"),
                ("isLive", "false"),
                ("storePassword", storePassword));
            Assert.Contains("Store saved", saved);
            Assert.Contains("Sandbox: test money", saved);
            Assert.DoesNotContain(storePassword, saved);

            // Kept sealed, never as typed
            var stored = await QueryAsync("rival", async db =>
                ((await db.MailServers.AsNoTracking().SingleAsync(Cancel)).Password, (await db.GatewayStores.AsNoTracking().SingleAsync(Cancel)).StorePassword));
            Assert.DoesNotContain(mailPassword, stored.Item1);
            Assert.DoesNotContain(storePassword, stored.Item2);

            // The rival's emails and payments now go through them; the launch courier's still through the server's
            await using (var scope = await ScopeAsync("rival"))
            {
                var settings = scope.ServiceProvider.GetRequiredService<ConnectionSettings>();
                var mail = settings.Mail();
                var store = settings.Store();
                Assert.Equal(("smtp.invalid", mailPassword, "hello@rival.test"), (mail.Host, mail.Password, mail.From));
                Assert.Equal(("rivalstore01", storePassword, "https://sandbox.sslcommerz.com/"), (store.StoreId, store.StorePassword, store.BaseUrl));
            }

            await using (var scope = await ScopeAsync("onedrop"))
            {
                var settings = scope.ServiceProvider.GetRequiredService<ConnectionSettings>();
                Assert.False(settings.Mail().Configured);
                Assert.False(settings.Store().Configured);
            }

            // An empty password box keeps the saved one; live switches the gateway
            saved = await admin.SubmitAsync(
                "/Admin/Connections",
                "/Admin/Connections?handler=Store",
                ("storeId", "rivalstore02"),
                ("isLive", "true"),
                ("storePassword", ""));
            Assert.Contains("real money", saved);
            await using (var scope = await ScopeAsync("rival"))
            {
                var store = scope.ServiceProvider.GetRequiredService<ConnectionSettings>().Store();
                Assert.Equal(("rivalstore02", storePassword, "https://securepay.sslcommerz.com/"), (store.StoreId, store.StorePassword, store.BaseUrl));
            }

            // A store needs its password the first time; a refusal says why
            Assert.Contains("Enter the address the emails come from", await admin.SubmitAsync(
                "/Admin/Connections",
                "/Admin/Connections?handler=Mail",
                ("host", "smtp.invalid"),
                ("port", "587"),
                ("fromAddress", "nobody"),
                ("password", "")));

            // A mail server that does not answer says so, rather than claiming the email went
            Assert.Contains("The mail server refused it", await admin.SubmitAsync(
                "/Admin/Connections",
                "/Admin/Connections?handler=TestMail",
                ("to", "admin@rival.test")));

            // Another courier's super admin sees none of it
            var onedrop = await SignInAsync("onedrop", "admin@onedrop.test");
            var theirs = await onedrop.PageAsync("/Admin/Connections");
            Assert.DoesNotContain("rivalstore02", theirs);
            Assert.DoesNotContain("smtp.invalid", theirs);

            // Super admins only: head office staff are refused the page and not offered it
            var accounts = await SignInAsync("onedrop", "accounts@onedrop.test");
            var refused = await accounts.GetAsync("/Admin/Connections");
            Assert.True(
                refused.Headers.Location?.ToString().Contains("AccessDenied") == true,
                $"/Admin/Connections answered {(int)refused.StatusCode} {refused.Headers.Location}");
            Assert.DoesNotContain("/Admin/Connections", await accounts.PageAsync("/Admin/Payouts"));
        }
        finally
        {
            await admin.PostFormAsync("/Admin/Connections", "/Admin/Connections?handler=RemoveMail");
            await admin.PostFormAsync("/Admin/Connections", "/Admin/Connections?handler=RemoveStore");
        }

        await using (var scope = await ScopeAsync("rival"))
        {
            var settings = scope.ServiceProvider.GetRequiredService<ConnectionSettings>();
            Assert.False(settings.Mail().Configured);
            Assert.False(settings.Store().Configured);
        }

        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync("/Admin/Connections")).StatusCode);
    }
}
