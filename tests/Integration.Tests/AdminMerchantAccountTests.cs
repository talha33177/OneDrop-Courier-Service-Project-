using System.Net;
using Microsoft.EntityFrameworkCore;

namespace Integration.Tests;

/// <summary>The whole merchant account on the admin's merchant page: its businesses, people, saved accounts and figures.</summary>
public class AdminMerchantAccountTests(WebAppFactory factory) : AppTests(factory)
{
    [Fact]
    public async Task The_admin_sees_the_account_s_businesses_moderators_and_saved_payout_accounts_from_any_business_of_it()
    {
        WebAppFactory.RequireDatabase();
        var shop = await NewMerchantAsync();
        var owner = await SignInAsync("onedrop", shop.Email);
        var business = $"Second shop {Guid.NewGuid():N}"[..24];
        var areaId = await QueryAsync("onedrop", db => db.Areas.Where(a => a.Name == "Dhanmondi").Select(a => a.Id).SingleAsync(Cancel));
        Assert.Equal(HttpStatusCode.Redirect, (await owner.PostFormAsync(
            "/Merchant/NewBusiness",
            "/Merchant/NewBusiness",
            ("Input.Name", business),
            ("Input.Phone", NewPhone()),
            ("Input.Address", "Road 2, Dhanmondi"),
            ("Input.PickupAreaId", $"{areaId}"),
            ("Input.PickupAddress", "Road 2, Dhanmondi"))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await owner.PostFormAsync(
            "/Merchant/Moderators",
            "/Merchant/Moderators?handler=Add",
            ("Name", "Nusrat Jahan"),
            ("Email", $"mod-{Guid.NewGuid():N}@shop.test"),
            ("Phone", NewPhone()),
            ("Permissions", "Dashboard"),
            ("Permissions", "Payments"))).StatusCode);
        var nagad = NewPhone();
        await owner.SubmitAsync("/Merchant/Settings", "/Merchant/Settings?handler=Payout", ("method", "Nagad"), ("payoutAccount", nagad), ("accountName", "Test Owner"), ("use", "false"));
        await BookAsync(shop.ApiKey);
        var businessId = await QueryAsync("onedrop", db => db.Merchants.Where(m => m.Name == business).Select(m => m.Id).SingleAsync(Cancel));
        var admin = await SignInAsync("onedrop", "admin@onedrop.test");

        var page = await admin.PageAsync($"/Admin/Merchant/{shop.Id}");
        Assert.Contains("2 businesses under one login", page);
        Assert.Contains(business, page);
        Assert.Contains("Nusrat Jahan", page);
        Assert.Contains("Dashboard, Payments", page);
        Assert.Contains($"Nagad {nagad}", page);
        Assert.Contains("No parcel finished yet", page);
        Assert.Contains("The merchant has asked for nothing", page);
        Assert.Contains("Nothing paid out yet", page);

        // The added business shows the same account: its people and saved accounts are the account's
        var other = await admin.PageAsync($"/Admin/Merchant/{businessId}");
        Assert.Contains("Nusrat Jahan", other);
        Assert.Contains($"Nagad {nagad}", other);
        Assert.Contains(shop.Name, other);
    }
}
