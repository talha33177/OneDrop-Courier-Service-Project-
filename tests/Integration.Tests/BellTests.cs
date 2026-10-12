using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Infrastructure.Payments;

namespace Integration.Tests;

/// <summary>
/// The merchant's bell: what happened to its parcels and money, and what is new since it last looked; and the admin's,
/// what waits for them.
/// </summary>
public partial class BellTests(WebAppFactory factory) : AppTests(factory)
{
    [Fact]
    public async Task The_bell_tells_the_merchant_what_happened_and_counts_only_what_came_since_it_looked()
    {
        WebAppFactory.RequireDatabase();
        var shop = await NewMerchantAsync(area: "Banani");
        var rider = await NewRiderAsync("GUL");
        var delivered = await BookAsync(shop.ApiKey, area: "Gulshan 2", cod: 1000);
        var refused = await BookAsync(shop.ApiKey, area: "Gulshan 2", cod: 800);
        var flagged = await BookAsync(shop.ApiKey, area: "Gulshan 2", cod: 500);
        var hub = await SignInAsync("onedrop", "hub@onedrop.test");
        foreach (var code in new[] { delivered, refused, flagged })
        {
            await hub.SubmitAsync("/Hub/Scan?hub=GUL", "/Hub/Scan?mode=Receive&hub=GUL", ("Code", code));
        }

        await hub.SubmitAsync("/Hub/Assign?hub=GUL", "/Hub/Assign?hub=GUL", ("riderId", $"{rider.Id}"), ("codes", delivered), ("codes", refused));
        var door = await SignInAsync("onedrop", rider.Email);
        await door.SubmitAsync($"/Rider/Delivery/{delivered}", $"/Rider/Delivery/{delivered}?handler=Deliver", ("collected", "1000"), ("reason", ""));
        await door.SubmitAsync($"/Rider/Delivery/{refused}", $"/Rider/Delivery/{refused}?handler=Refuse", ("reason", "Customer not at home"));
        await hub.SubmitAsync($"/Hub/Parcel/{flagged}", $"/Hub/Parcel/{flagged}?handler=Flag", ("issue", "Exceptional"), ("note", "Box torn open"));
        var owner = await SignInAsync("onedrop", shop.Email);
        await owner.SubmitAsync("/Merchant/Payments", "/Merchant/Payments?handler=Ask");
        var request = await QueryAsync("onedrop", db => db.PayoutRequests.Where(r => r.MerchantId == shop.Id).Select(r => r.Id).SingleAsync(Cancel));
        await (await SignInAsync("onedrop", "admin@onedrop.test")).SubmitAsync("/Admin/Payouts", $"/Admin/Payouts?handler=PayRequest&id={request}");

        var page = await owner.PageAsync("/Merchant/Parcels");
        Assert.Contains("parcel delivered on", page);
        Assert.Contains($"{refused} was refused at the door: Customer not at home", page);
        Assert.Contains($"{flagged} is exceptional: Box torn open", page);
        Assert.Matches(@"Payout INV-\d+ of ৳930 was sent to bKash 01", page);
        Assert.Matches(@"<span class=""bell-count"" data-bell-count>4</span>", page);

        // Opened, the bell keeps the time of its newest notice: nothing is new until something else happens
        var latest = Latest().Match(page).Groups[1].Value;
        var userId = await QueryAsync("onedrop", db => db.Users.Where(u => u.Email == shop.Email).Select(u => u.Id).SingleAsync(Cancel));
        var seen = await (await owner.SendAsync(HttpMethod.Get, "/Merchant/Parcels", null, $"bell-{userId}={latest}")).Content.ReadAsStringAsync(Cancel);
        Assert.DoesNotContain("data-bell-count", seen);
        Assert.Contains("is exceptional", seen);

        var other = await SignInAsync("onedrop", (await NewMerchantAsync(area: "Banani")).Email);
        var theirs = await other.PageAsync("/Merchant");
        Assert.DoesNotContain(refused, theirs);
        Assert.Contains("Nothing yet.", theirs);
    }

    [Fact]
    public async Task The_admins_bell_lists_what_waits_for_them_and_only_their_couriers()
    {
        WebAppFactory.RequireDatabase();
        await NewMerchantAsync(approved: false);
        var shop = await NewMerchantAsync();
        var account = await QueryAsync("onedrop", db => db.Merchants.Where(m => m.Id == shop.Id).Select(m => m.PayoutAccount!).SingleAsync(Cancel));
        var gateway = Factory.Services.GetRequiredService<FakePayoutLog>();
        var admin = await SignInAsync("onedrop", "admin@onedrop.test");
        var page = $"/Admin/Merchant/{shop.Id}";
        await admin.SubmitAsync(page, $"{page}?handler=Adjust", ("direction", "credit"), ("amount", "200"), ("note", "Bell check"));
        gateway.Refuse(account, "Bell check refusal.");
        try
        {
            await admin.SubmitAsync(page, $"{page}?handler=PayNow");
        }
        finally
        {
            gateway.StopRefusing(account);
        }

        var bell = await admin.PageAsync("/Admin");
        Assert.Contains(AdminBellHead, bell);
        Assert.Matches(@"sign-ups? waiting for your approval", bell);
        Assert.Matches(@"payouts? of ৳[0-9,]+ refused by the gateway", bell);
        Assert.Contains("/Admin/Merchants?status=Pending", bell);

        // Hub staff and merchants have no admin bell; another courier's admin sees only its own
        Assert.DoesNotContain(AdminBellHead, await (await SignInAsync("onedrop", "hub@onedrop.test")).PageAsync("/Hub/Choose"));
        Assert.DoesNotContain(AdminBellHead, await (await SignInAsync("onedrop", shop.Email)).PageAsync("/Merchant"));
        Assert.DoesNotContain("refused by the gateway", await (await SignInAsync("rival", "admin@rival.test")).PageAsync("/Admin"));
    }

    [Fact]
    public async Task The_menu_marks_where_work_waits_for_each_person()
    {
        WebAppFactory.RequireDatabase();
        var shop = await NewMerchantAsync(area: "Banani");
        var other = await SignInAsync("onedrop", (await NewMerchantAsync(area: "Banani")).Email);
        var rider = await NewRiderAsync("GUL");
        var owner = await SignInAsync("onedrop", shop.Email);
        var door = await SignInAsync("onedrop", rider.Email);
        Assert.Empty(Marked(await owner.PageAsync("/Merchant")));
        Assert.Empty(Marked(await door.PageAsync("/Rider")));

        // Booked: it goes with the day's pickup by itself, so nothing waits for the merchant
        var code = await BookAsync(shop.ApiKey, area: "Gulshan 2", cod: 700);
        Assert.Empty(Marked(await owner.PageAsync("/Merchant")));
        Assert.Empty(Marked(await other.PageAsync("/Merchant")));

        // At the hub and given to the rider: the rider's deliveries wait, and the parcel no longer waits for a pickup
        var hub = await SignInAsync("onedrop", "hub@onedrop.test");
        await hub.SubmitAsync("/Hub/Scan?hub=GUL", "/Hub/Scan?mode=Receive&hub=GUL", ("Code", code));
        await hub.SubmitAsync("/Hub/Assign?hub=GUL", "/Hub/Assign?hub=GUL", ("riderId", $"{rider.Id}"), ("codes", code));
        var riderPage = await door.PageAsync("/Rider");
        Assert.Equal(["/Rider"], Marked(riderPage));
        Assert.Contains("""class="menu-toggle" title="Menu">""", riderPage);
        Assert.Empty(Marked(await owner.PageAsync("/Merchant")));

        await NewMerchantAsync(approved: false);
        Assert.Contains("/Admin/Merchants", Marked(await (await SignInAsync("onedrop", "admin@onedrop.test")).PageAsync("/Admin")));
    }

    /// <summary>The menu items carrying the "work waiting" mark.</summary>
    private static List<string> Marked(string page)
    {
        return [.. MarkedItem().Matches(page).Select(match => match.Groups[1].Value)];
    }

    [GeneratedRegex("""class="nav-link[^"]*" href="([^"]+)">(?:(?!</a>).)*?class="nav-work""", RegexOptions.Singleline)]
    private static partial Regex MarkedItem();

    private const string AdminBellHead ="""<div class="bell-head">Waiting for you</div>""";

    [GeneratedRegex(@"data-bell-latest=""([0-9]+)""")]
    private static partial Regex Latest();
}
