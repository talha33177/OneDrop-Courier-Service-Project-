using Domain.Notifications;
using Domain.Payments;

namespace Domain.Tests;

/// <summary>The courier's own mail server and SSLCommerz store: checked when typed, and the password kept unless replaced.</summary>
public class ConnectionTests
{
    private static readonly MailServerDetails Gmail = new("smtp.gmail.com", 587, null, "shop@gmail.com", "OneDrop Courier");

    [Fact]
    public void A_mail_server_needs_a_server_a_port_a_from_address_and_a_password()
    {
        var server = MailServer.Create(Gmail with { Host = "  smtp.gmail.com " }, "sealed-1").Value;
        Assert.Equal(("smtp.gmail.com", 587, "shop@gmail.com", "sealed-1"), (server.Host, server.Port, server.FromAddress, server.Password));

        Assert.Equal("mail.host", MailServer.Create(Gmail with { Host = " " }, "sealed").Error?.Code);
        Assert.Equal("mail.host", MailServer.Create(Gmail with { Host = "smtp gmail.com" }, "sealed").Error?.Code);
        Assert.Equal("mail.port", MailServer.Create(Gmail with { Port = 0 }, "sealed").Error?.Code);
        Assert.Equal("mail.port", MailServer.Create(Gmail with { Port = 70000 }, "sealed").Error?.Code);
        Assert.Equal("mail.from", MailServer.Create(Gmail with { FromAddress = "shop.gmail.com" }, "sealed").Error?.Code);
        Assert.Equal("mail.from", MailServer.Create(Gmail with { FromAddress = "shop@" }, "sealed").Error?.Code);
    }

    [Fact]
    public void A_mail_server_keeps_its_password_when_none_is_typed_and_takes_a_new_one_when_it_is()
    {
        var server = MailServer.Create(Gmail, "sealed-1").Value;

        Assert.True(server.Change(Gmail with { Port = 465, UserName = "login@gmail.com" }, null).IsSuccess);
        Assert.Equal((465, "login@gmail.com", "sealed-1"), (server.Port, server.UserName, server.Password));

        Assert.True(server.Change(Gmail, "sealed-2").IsSuccess);
        Assert.Equal("sealed-2", server.Password);

        // A refused change leaves everything as it was
        Assert.Equal("mail.from", server.Change(Gmail with { FromAddress = "nobody", Port = 25 }, "sealed-3").Error?.Code);
        Assert.Equal((587, "sealed-2"), (server.Port, server.Password));
    }

    [Fact]
    public void A_mail_server_can_be_switched_off_made_insecure_and_lose_its_password()
    {
        var server = MailServer.Create(Gmail, "sealed-1").Value;
        Assert.Equal((true, true, false, true), (server.IsEnabled, server.UseSsl, server.AllowSelfSigned, server.HasPassword));

        // Off keeps the details; a self-signed certificate is only a question on a secure connection
        Assert.True(server.Change(Gmail with { IsEnabled = false, AllowSelfSigned = true }, null).IsSuccess);
        Assert.Equal((false, true, true, "smtp.gmail.com"), (server.IsEnabled, server.UseSsl, server.AllowSelfSigned, server.Host));
        Assert.True(server.Change(Gmail with { UseSsl = false, AllowSelfSigned = true }, null).IsSuccess);
        Assert.Equal((false, false), (server.UseSsl, server.AllowSelfSigned));

        // A server that takes mail without a login: the password goes, and saving again does not ask for one
        server.ClearPassword();
        Assert.False(server.HasPassword);
        Assert.True(server.Change(Gmail, null).IsSuccess);
        Assert.False(server.HasPassword);
        Assert.True(server.Change(Gmail, "sealed-2").IsSuccess);
        Assert.True(server.HasPassword);
    }

    [Fact]
    public void A_store_needs_its_id_and_a_password_and_keeps_the_password_when_none_is_typed()
    {
        var none = new StoreCredentials(null, null);
        var store = GatewayStore.Create(new StoreCredentials(" onedr6ac6fd8338cc6 ", "sealed-1"), none, false).Value;
        Assert.Equal(("onedr6ac6fd8338cc6", "sealed-1"), store.InUse);
        Assert.False(store.HasLive);

        Assert.Equal("store.sandbox.id", GatewayStore.Create(new StoreCredentials("", "sealed"), none, false).Error?.Code);
        Assert.Equal("store.sandbox.id", GatewayStore.Create(new StoreCredentials("store id", "sealed"), none, false).Error?.Code);
        Assert.Equal("store.sandbox.password", GatewayStore.Create(new StoreCredentials("onedr01", null), none, false).Error?.Code);
        Assert.Equal("store.live.id", store.Change(new StoreCredentials("onedr6ac6fd8338cc6", null), new StoreCredentials("store@ssl", "sealed-2"), false).Error?.Code);
        Assert.Equal(("onedr6ac6fd8338cc6", "sealed-1"), store.InUse);
    }

    [Fact]
    public void Both_stores_are_kept_and_only_the_one_picked_is_in_use()
    {
        var none = new StoreCredentials(null, null);
        var sandbox = new StoreCredentials("onedrsandbox01", null);
        var store = GatewayStore.Create(new StoreCredentials("onedrsandbox01", "sealed-sandbox"), none, false).Value;

        // Live cannot be picked before it has its details
        Assert.Equal("store.live", store.Change(sandbox, none, true).Error?.Code);
        Assert.False(store.IsLive);

        // Adding the live store keeps the sandbox one, and switching back and forth needs no password typed again
        Assert.True(store.Change(sandbox, new StoreCredentials("onedrlive01", "sealed-live"), true).IsSuccess);
        Assert.Equal(("onedrlive01", "sealed-live"), store.InUse);
        Assert.Equal(("onedrsandbox01", "sealed-sandbox"), (store.SandboxStoreId, store.SandboxPassword));
        Assert.True(store.Change(sandbox, new StoreCredentials("onedrlive01", null), false).IsSuccess);
        Assert.Equal(("onedrsandbox01", "sealed-sandbox"), store.InUse);
        Assert.Equal(("onedrlive01", "sealed-live"), (store.LiveStoreId, store.LivePassword));

        // A store cleared is taken off, but never the one in use
        Assert.Equal("store.sandbox", store.Change(none, new StoreCredentials("onedrlive01", null), false).Error?.Code);
        Assert.True(store.Change(none, new StoreCredentials("onedrlive01", null), true).IsSuccess);
        Assert.Equal((false, null, null), (store.HasSandbox, store.SandboxStoreId, store.SandboxPassword));
        Assert.Equal(("onedrlive01", "sealed-live"), store.InUse);
    }
}
