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
    public void A_store_needs_its_id_and_a_password_and_keeps_the_password_when_none_is_typed()
    {
        var store = GatewayStore.Create(" onedr6ac6fd8338cc6 ", false, "sealed-1").Value;
        Assert.Equal(("onedr6ac6fd8338cc6", false, "sealed-1"), (store.StoreId, store.IsLive, store.StorePassword));

        Assert.True(store.Change("onedrlive01", true, null).IsSuccess);
        Assert.Equal(("onedrlive01", true, "sealed-1"), (store.StoreId, store.IsLive, store.StorePassword));

        Assert.Equal("store.id", GatewayStore.Create("", false, "sealed").Error?.Code);
        Assert.Equal("store.id", GatewayStore.Create("store id", false, "sealed").Error?.Code);
        Assert.Equal("store.id", store.Change("store@ssl", false, "sealed-2").Error?.Code);
        Assert.Equal("sealed-1", store.StorePassword);
    }
}
