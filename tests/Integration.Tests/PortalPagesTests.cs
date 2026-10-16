using System.Net;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Domain.Delivery;
using Domain.Parcels;

namespace Integration.Tests;

/// <summary>
/// Each role's home, the front page, public tracking, the admin's riders and rate card, and a pickup from request to
/// collection.
/// </summary>
public partial class PortalPagesTests(WebAppFactory factory) : AppTests(factory)
{
    [Theory]
    [InlineData("admin@onedrop.test", "/Admin")]
    [InlineData("hub@onedrop.test", "/Hub")]
    [InlineData("fashion@onedrop.test", "/Merchant")]
    [InlineData("rider@onedrop.test", "/Rider")]
    public async Task Each_role_lands_on_its_own_home(string email, string home)
    {
        WebAppFactory.RequireDatabase();
        var visitor = await SignInAsync("onedrop", email);

        var landing = await visitor.GetAsync("/");

        Assert.Equal(HttpStatusCode.Redirect, landing.StatusCode);
        Assert.Equal(home, landing.Headers.Location!.OriginalString);
        var page = await visitor.GetAsync(home);
        if (home == "/Hub")
        {
            // Hub staff first choose the hub they work at
            Assert.Equal("/Hub/Choose?next=Index", page.Headers.Location!.OriginalString);
            page = await visitor.GetAsync(page.Headers.Location.OriginalString);
        }

        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
    }

    [Fact]
    public async Task Hub_staff_choose_their_hub_after_signing_in_and_see_where_pickups_wait()
    {
        WebAppFactory.RequireDatabase();
        var shop = await NewMerchantAsync(area: "Mirpur 10");
        // Booking puts the merchant's point on the Mirpur hub's pickup list
        await BookAsync(shop.ApiKey);
        var hub = await SignInAsync("onedrop", "hub@onedrop.test");

        var asked = await hub.GetAsync("/Hub/Pickups");
        Assert.Equal(HttpStatusCode.Redirect, asked.StatusCode);
        Assert.Equal("/Hub/Choose?next=Pickups", asked.Headers.Location!.OriginalString);

        // The admin's "Pickups open" tile: straight to the hub when only one has pickups, else every hub with its count
        var choose = await hub.GetAsync("/Hub/Choose?next=Pickups");
        if (choose.StatusCode == HttpStatusCode.Redirect)
        {
            Assert.Equal("/Hub/Pickups?hub=MIR", choose.Headers.Location!.OriginalString);
        }
        else
        {
            var cards = WebUtility.HtmlDecode(await choose.Content.ReadAsStringAsync(Cancel));
            Assert.Contains("/Hub/Pickups?hub=MIR", cards);
            Assert.Contains("Mirpur hub", cards);
        }

        Assert.Contains(shop.Name, await hub.PageAsync("/Hub/Pickups?hub=MIR"));
        var remembered = Visit("onedrop", $"{await SignInCookieAsync("onedrop", "hub@onedrop.test")}; {Web.Pages.Hub.RememberHub.Cookie}=MIR");
        Assert.Contains(shop.Name, await remembered.PageAsync("/Hub/Pickups"));
    }

    [Fact]
    public async Task Admins_open_one_board_for_every_hub_and_pick_a_hub_from_its_menu()
    {
        WebAppFactory.RequireDatabase();
        var shop = await NewMerchantAsync(area: "Mirpur 10");
        await BookAsync(shop.ApiKey);
        var admin = await SignInAsync("onedrop", "admin@onedrop.test");

        // Before choosing, the admin sees every hub; its steps open the chooser, and the hub menu offers every hub
        var all = await admin.PageAsync("/Hub");
        Assert.Contains("<h1>All hubs</h1>", all);
        Assert.Matches("<a href=\"\\?hub=\\*\" class=\"current[^\"]*\">\\s*<span>All hubs</span>", all);
        Assert.Contains("/Hub/Choose?next=Pickups", all);
        Assert.Contains("/Hub/Choose?next=Scan&mode=Receive", all);
        Assert.Contains("<a href=\"?hub=MIR\"", all);
        Assert.Contains("<span>Mirpur hub (MIR)</span>", all);

        var one = await admin.PageAsync("/Hub?hub=MIR");
        Assert.Contains("<h1>Mirpur hub</h1>", one);
        Assert.Contains("/Hub/Pickups?hub=MIR", one);
        var remembered = Visit("onedrop", $"{await SignInCookieAsync("onedrop", "admin@onedrop.test")}; {Web.Pages.Hub.RememberHub.Cookie}=*");
        Assert.Contains("<h1>All hubs</h1>", await remembered.PageAsync("/Hub"));

        // Hub staff still choose first, and may ask for every hub; a page that works at one hub cannot show them all
        var hub = await SignInAsync("onedrop", "hub@onedrop.test");
        Assert.Equal("/Hub/Choose?next=Index", (await hub.GetAsync("/Hub")).Headers.Location!.OriginalString);
        Assert.Contains("<h1>All hubs</h1>", await hub.PageAsync("/Hub?hub=*"));
        Assert.Equal("/Hub/Choose?next=Scan", (await hub.GetAsync("/Hub/Scan?hub=*")).Headers.Location!.OriginalString);

        // Another courier's board of every hub holds only its own hub
        var rival = await (await SignInAsync("rival", "admin@rival.test")).PageAsync("/Hub");
        Assert.Contains("<h1>All hubs</h1>", rival);
        Assert.DoesNotContain("Mirpur hub", rival);
    }

    [Fact]
    public async Task Anyone_tracks_a_parcel_by_its_code_at_its_own_courier_only()
    {
        WebAppFactory.RequireDatabase();
        var shop = await NewMerchantAsync();
        var code = await BookAsync(shop.ApiKey, area: "Banani");

        var page = await Visit("onedrop").PageAsync($"/Track?code={code}");
        Assert.Contains(code, page);
        Assert.Contains("Pending pickup", page);
        Assert.DoesNotContain("Rahim Uddin", page);
        Assert.DoesNotContain("House 22", page);

        Assert.Contains("could not find a parcel", await Visit("rival").PageAsync($"/Track?code={code}"));
        Assert.Contains("could not find a parcel", await Visit("onedrop").PageAsync("/Track?code=OD99999999"));
        Assert.Equal(HttpStatusCode.NotFound, (await Visit("").GetAsync($"/Track?code={code}")).StatusCode);
        Assert.Contains("Sylhet Sadar", await Visit("onedrop").PageAsync("/Coverage"));
    }

    [Fact]
    public async Task The_front_page_shows_the_courier_with_its_drawings_rates_and_a_tracking_box()
    {
        WebAppFactory.RequireDatabase();

        var page = await Visit("onedrop").PageAsync("/");

        Assert.Contains("OneDrop Courier", page);
        Assert.Contains("class=\"brand-mark\"", page);
        Assert.Contains("action=\"/Track\"", page);
        Assert.Contains("class=\"scene art\"", page);
        Assert.Contains("art-paid", page);
        Assert.Matches(Landing(), page);
        Assert.Contains("Inside city", page);
    }

    [Fact]
    public async Task The_bare_domain_sends_visitors_to_the_home_courier_and_platform_staff_still_sign_in_there()
    {
        WebAppFactory.RequireDatabase();

        var visitor = await Visit("").GetAsync("/");
        Assert.Equal(HttpStatusCode.Redirect, visitor.StatusCode);
        Assert.Equal("http://onedrop.localhost/", visitor.Headers.Location!.ToString());

        Assert.Equal(HttpStatusCode.OK, (await Visit("").GetAsync("/Account/Login")).StatusCode);
        var staff = await SignInAsync("", "admin@platform.test");
        var home = await staff.GetAsync("/");
        Assert.Equal("/Platform/Tenants", home.Headers.Location!.ToString());
        Assert.Contains("onedrop", await staff.PageAsync("/Platform/Tenants"));
    }

    [Fact]
    public async Task Every_icon_the_rider_app_installs_with_exists()
    {
        WebAppFactory.RequireDatabase();
        var site = Visit("onedrop");
        var manifest = await site.PageAsync("/rider.webmanifest");
        var icons = IconSource().Matches(manifest).Select(match => match.Groups[1].Value).ToList();

        Assert.NotEmpty(icons);
        foreach (var icon in icons)
        {
            Assert.Equal(HttpStatusCode.OK, (await site.GetAsync(icon)).StatusCode);
        }
    }

    // The front page's own stylesheet, by its name with or without the fingerprint static assets may add
    [GeneratedRegex(@"/css/landing(\.\w+)?\.css")]
    private static partial Regex Landing();

    [GeneratedRegex("\"src\": \"([^\"]+)\"")]
    private static partial Regex IconSource();

    [Fact]
    public async Task The_admin_adds_a_rider_who_signs_in_to_the_rider_app_and_another_couriers_hub_is_refused()
    {
        WebAppFactory.RequireDatabase();
        var admin = await SignInAsync("onedrop", "admin@onedrop.test");
        var email = $"{Guid.NewGuid():N}@rider.test";
        var name = $"Rider {Guid.NewGuid():N}"[..14];
        var gul = await QueryAsync("onedrop", db => db.Hubs.Where(h => h.Code == "GUL").Select(h => h.Id).SingleAsync(Cancel));
        var rivalHub = await QueryAsync("rival", db => db.Hubs.Select(h => h.Id).FirstAsync(Cancel));

        var refused = await admin.SubmitAsync("/Admin/Riders", "/Admin/Riders?handler=Add", ("name", name), ("phone", NewPhone()), ("hubId", $"{rivalHub}"), ("vehicle", "Bicycle"), ("email", email), ("password", WebAppFactory.Password));
        Assert.DoesNotContain($"{name} is added", refused);
        Assert.Contains("Choose what the rider rides", await admin.SubmitAsync("/Admin/Riders", "/Admin/Riders?handler=Add", ("name", name), ("phone", NewPhone()), ("hubId", $"{gul}"), ("vehicle", ""), ("email", email), ("password", WebAppFactory.Password)));
        Assert.False(await QueryAsync("onedrop", db => db.Riders.AnyAsync(r => r.Name == name, Cancel)));

        Assert.Contains($"{name} is added", await admin.SubmitAsync("/Admin/Riders", "/Admin/Riders?handler=Add", ("name", name), ("phone", NewPhone()), ("hubId", $"{gul}"), ("vehicle", "Bicycle"), ("email", email), ("password", WebAppFactory.Password)));
        Assert.Equal(Vehicle.Bicycle, await QueryAsync("onedrop", db => db.Riders.Where(r => r.Name == name).Select(r => r.Vehicle).SingleAsync(Cancel)));
        var rider = await SignInAsync("onedrop", email);
        Assert.Equal(HttpStatusCode.OK, (await rider.GetAsync("/Rider")).StatusCode);
        Assert.NotEqual(HttpStatusCode.OK, (await rider.GetAsync("/Admin")).StatusCode);
        Assert.NotEqual(HttpStatusCode.OK, (await rider.GetAsync("/Hub")).StatusCode);
    }

    [Fact]
    public async Task Only_the_admin_sees_and_changes_the_rate_card_and_merchants_see_it_read_only()
    {
        WebAppFactory.RequireDatabase();
        var admin = await SignInAsync("onedrop", "admin@onedrop.test");
        var hub = await SignInAsync("onedrop", "hub@onedrop.test");
        var shop = await NewMerchantAsync();

        Assert.Contains("Outside city", await admin.PageAsync("/Admin/Rates"));
        Assert.NotEqual(HttpStatusCode.OK, (await hub.GetAsync("/Admin/Rates")).StatusCode);
        await hub.PostFormAsync("/Hub?hub=MIR", "/Admin/Rates", ("area", "InsideCity"), ("includedKg", "1"), ("baseCharge", "1"), ("extraKgCharge", "1"), ("codChargePercent", "0"), ("returnCharge", "0"));
        Assert.Contains("৳120", await (await SignInAsync("onedrop", shop.Email)).PageAsync("/Merchant/Pricing"));
        Assert.Equal(60, await QueryAsync("onedrop", db => db.DeliveryRates.Where(r => r.ServiceArea == Domain.Pricing.ServiceArea.InsideCity).Select(r => r.BaseCharge).SingleAsync(Cancel)));
    }

    [Fact]
    public async Task A_booked_parcel_goes_on_the_days_pickup_the_hub_sends_a_rider_and_the_rider_collects_the_parcels()
    {
        WebAppFactory.RequireDatabase();
        var shop = await NewMerchantAsync(area: "Mirpur 10");
        var rider = await NewRiderAsync("MIR", vehicle: Vehicle.Van);
        var codes = new[] { await BookAsync(shop.ApiKey), await BookAsync(shop.ApiKey) };

        // Booking listed the point once for its day; the merchant asks for nothing
        var pickup = await QueryAsync("onedrop", db => db.PickupRequests.SingleAsync(p => p.MerchantId == shop.Id, Cancel));

        var hub = await SignInAsync("onedrop", "hub@onedrop.test");
        Assert.Contains(shop.Name, await hub.PageAsync("/Hub/Pickups?hub=MIR"));
        Assert.Equal(HttpStatusCode.NotFound, (await hub.PostFormAsync("/Hub/Pickups?hub=MIR", "/Hub/Pickups?hub=GUL&handler=Assign", ("id", $"{pickup.Id}"), ("riderId", $"{rider.Id}"))).StatusCode);
        Assert.Contains("Rider assigned", await hub.SubmitAsync("/Hub/Pickups?hub=MIR", "/Hub/Pickups?hub=MIR&handler=Assign", ("id", $"{pickup.Id}"), ("riderId", $"{rider.Id}")));

        var app = await SignInAsync("onedrop", rider.Email);
        Assert.Contains(shop.Name, await app.PageAsync("/Rider/Pickups"));
        Assert.Contains("2 parcels collected", await app.SubmitAsync("/Rider/Pickups", "/Rider/Pickups", ("id", $"{pickup.Id}"), ("codes", codes[0]), ("codes", codes[1]), ("shown", codes[0]), ("shown", codes[1])));

        foreach (var code in codes)
        {
            Assert.Equal(ParcelStatus.PickedUp, (await ParcelAsync(code)).Status);
        }

        Assert.Equal(PickupStatus.Completed, await QueryAsync("onedrop", db => db.PickupRequests.Where(p => p.Id == pickup.Id).Select(p => p.Status).SingleAsync(Cancel)));

        // Another merchant does not see it
        var other = await SignInAsync("onedrop", (await NewMerchantAsync()).Email);
        Assert.DoesNotContain(rider.Email[..6], await other.PageAsync("/Merchant/Pickups"));
    }
}
