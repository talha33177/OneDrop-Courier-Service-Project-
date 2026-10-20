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
[Collection("Emails")]
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
                ("fromName", "Rival Courier"),
                ("isEnabled", "true"),
                ("useSsl", "true"));
            Assert.Contains("Email settings saved", saved);
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
                // OneDrop may have a server of its own in the test database (the owner's, saved through the page)
                Assert.NotEqual("smtp.invalid", settings.Mail().Host);
                Assert.NotEqual("rivalstore01", settings.Store().StoreId);
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

    [Fact]
    public async Task The_email_sender_can_be_switched_off_tried_before_saving_secured_and_have_its_password_deleted()
    {
        WebAppFactory.RequireDatabase();
        var admin = await SignInAsync("rival", "admin@rival.test");
        const string password = "abcdefgh1234wxyz";
        (string, string)[] server(params (string, string)[] more) =>
        [
            ("fromName", "Rival Courier"),
            ("fromAddress", "hello@rival.test"),
            ("host", "mail.rival.test"),
            ("port", "587"),
            ("userName", ""),
            .. more
        ];

        try
        {
            // Saved on, secure, with a self-signed certificate allowed: the page shows the password's ends and no more
            var saved = await admin.SubmitAsync(
                "/Admin/Connections",
                "/Admin/Connections?handler=Mail",
                [.. server(("password", password), ("isEnabled", "true"), ("isEnabled", "false"), ("useSsl", "true"), ("useSsl", "false"), ("allowSelfSigned", "true"), ("allowSelfSigned", "false"))]);
            Assert.Contains("Email settings saved", saved);
            Assert.Contains("ab••••••yz", saved);
            Assert.DoesNotContain(password, saved);
            Assert.DoesNotContain("1234", saved);
            var kept = await QueryAsync("rival", db => db.MailServers.AsNoTracking().SingleAsync(Cancel));
            Assert.Equal((true, true, true), (kept.IsEnabled, kept.UseSsl, kept.AllowSelfSigned));
            Assert.DoesNotContain(password, kept.Password);

            // Tried as typed, saving nothing: an empty password box tries the saved one, and the answer is the server's own
            var worked = await admin.PostFormAsync("/Admin/Connections", "/Admin/Connections?handler=CheckMail", [.. server(("password", ""), ("useSsl", "true"), ("allowSelfSigned", "true"))]);
            Assert.Contains("\"ok\":true", await worked.Content.ReadAsStringAsync(Cancel));
            Assert.Equal(("mail.rival.test", password, true, true), (RecordingMailTransports.LastChecked!.Host, RecordingMailTransports.LastChecked.Password, RecordingMailTransports.LastChecked.UseSsl, RecordingMailTransports.LastChecked.AllowSelfSigned));
            var refused = await admin.PostFormAsync(
                "/Admin/Connections",
                "/Admin/Connections?handler=CheckMail",
                ("host", "mail.nowhere.invalid"), ("port", "587"), ("fromAddress", "hello@rival.test"), ("password", "x"));
            var answer = await refused.Content.ReadAsStringAsync(Cancel);
            Assert.Contains("\"ok\":false", answer);
            Assert.Contains("did not accept the connection", answer);

            // A certificate is only a question on a secure connection
            await admin.SubmitAsync("/Admin/Connections", "/Admin/Connections?handler=Mail", [.. server(("isEnabled", "true"), ("useSsl", "false"), ("allowSelfSigned", "true"))]);
            kept = await QueryAsync("rival", db => db.MailServers.AsNoTracking().SingleAsync(Cancel));
            Assert.Equal((false, false), (kept.UseSsl, kept.AllowSelfSigned));

            // Off keeps the details and sends nothing, and the server's own settings do not take over
            var off = await admin.SubmitAsync("/Admin/Connections", "/Admin/Connections?handler=Mail", [.. server(("isEnabled", "false"), ("useSsl", "true"))]);
            Assert.Contains("Off: no emails are sent", off);
            await using (var scope = await ScopeAsync("rival"))
            {
                Assert.False(scope.ServiceProvider.GetRequiredService<ConnectionSettings>().Mail().Configured);
            }

            // Delete takes only the password: the server is then tried without a login
            var deleted = await admin.SubmitAsync("/Admin/Connections", "/Admin/Connections?handler=RemoveMailPassword");
            Assert.Contains("The password is removed", deleted);
            Assert.Contains("No password is saved", deleted);
            await admin.SubmitAsync("/Admin/Connections", "/Admin/Connections?handler=Mail", [.. server(("isEnabled", "true"), ("useSsl", "true"))]);
            await using (var scope = await ScopeAsync("rival"))
            {
                var mail = scope.ServiceProvider.GetRequiredService<ConnectionSettings>().Mail();
                Assert.Equal(("mail.rival.test", "", true), (mail.Host, mail.Password, mail.Configured));
            }
        }
        finally
        {
            await admin.PostFormAsync("/Admin/Connections", "/Admin/Connections?handler=RemoveMail");
        }

        Assert.Equal(0, await QueryAsync("rival", db => db.MailServers.CountAsync(Cancel)));
    }
}
