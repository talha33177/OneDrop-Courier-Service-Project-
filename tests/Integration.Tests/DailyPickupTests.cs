using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Application.Common;
using Application.Delivery.Pickups;
using Domain.Delivery;
using Domain.Parcels;

namespace Integration.Tests;

/// <summary>
/// The courier's one visit a day to a pickup point: booking puts the point on the list of its day, the rider collects
/// whatever is waiting when they reach the door (a parcel booked a minute before included), and once the van has been
/// to a point what is booked there goes with the next day's pickup. A pickup the day passed without is carried over.
/// The merchant asks for nothing. Merchants here collect in Old Dhaka (Motijheel hub), which no other test uses.
/// </summary>
public class DailyPickupTests(WebAppFactory factory) : AppTests(factory)
{
    private const string Area = "Lalbagh";
    private const string Hub = "MOT";

    [Fact]
    public async Task Booking_puts_the_point_on_its_days_list_once_and_the_merchant_asks_for_nothing()
    {
        WebAppFactory.RequireDatabase();
        var shop = await NewMerchantAsync(area: Area);
        await BookAsync(shop.ApiKey);
        await BookAsync(shop.ApiKey);

        var pickups = await PickupsAsync(shop.Id);
        var pickup = Assert.Single(pickups);
        Assert.Equal(Today, pickup.PickupDate);
        Assert.Equal(PickupStatus.Scheduled, pickup.Status);

        var merchant = await SignInAsync("onedrop", shop.Email);
        var page = WebUtility.HtmlDecode(await merchant.PageAsync("/Merchant/Pickups"));
        Assert.Contains("You do not need to ask", page);
        Assert.Contains("Waiting for a rider", page);
        Assert.DoesNotContain("Request a pickup", page);
        Assert.DoesNotContain("Request pickup", page);
        var menu = WebUtility.HtmlDecode(await merchant.PageAsync("/Merchant"));
        Assert.Contains("Manage businesses", menu);
        Assert.DoesNotMatch("class=\"nav-link[^\"]*\" href=\"/Merchant/NewBusiness\"", menu);
    }

    [Fact]
    public async Task A_parcel_booked_while_the_van_is_on_its_way_is_collected_with_it()
    {
        WebAppFactory.RequireDatabase();
        var shop = await NewMerchantAsync(area: Area);
        var van = await NewRiderAsync(Hub, vehicle: Vehicle.Van);
        var first = await BookAsync(shop.ApiKey);
        var pickup = Assert.Single(await PickupsAsync(shop.Id));
        var hub = await SignInAsync("onedrop", "hub@onedrop.test");
        Assert.Contains("Rider assigned", await hub.SubmitAsync($"/Hub/Pickups?hub={Hub}", $"/Hub/Pickups?handler=Assign&id={pickup.Id}&hub={Hub}", ("riderId", $"{van.Id}")));

        // The merchant books once more before the van reaches the door: the same pickup collects it
        var last = await BookAsync(shop.ApiKey);
        Assert.Single(await PickupsAsync(shop.Id));
        await CatchUpAsync(DateTime.UtcNow);
        Assert.Equal(PickupStatus.Assigned, Assert.Single(await PickupsAsync(shop.Id)).Status);

        var door = await SignInAsync("onedrop", van.Email);
        var list = await door.PageAsync("/Rider/Pickups");
        Assert.Contains(first, list);
        Assert.Contains(last, list);
        Assert.Contains("2 parcels collected", await CollectAsync(door, pickup.Id, [first, last], [first, last]));
        Assert.Equal(ParcelStatus.PickedUp, (await ParcelAsync(last)).Status);

        // Nothing was left behind, so nothing is listed for tomorrow
        Assert.Equal(PickupStatus.Completed, Assert.Single(await PickupsAsync(shop.Id)).Status);
    }

    [Fact]
    public async Task Once_the_van_has_been_to_a_merchant_what_is_booked_there_goes_the_next_day()
    {
        WebAppFactory.RequireDatabase();
        var shop = await NewMerchantAsync(area: Area);
        var van = await NewRiderAsync(Hub, vehicle: Vehicle.Van);
        var morning = await BookAsync(shop.ApiKey);
        var pickup = Assert.Single(await PickupsAsync(shop.Id));
        var hub = await SignInAsync("onedrop", "hub@onedrop.test");
        Assert.Contains("Rider assigned", await hub.SubmitAsync($"/Hub/Pickups?hub={Hub}", $"/Hub/Pickups?handler=Assign&id={pickup.Id}&hub={Hub}", ("riderId", $"{van.Id}")));
        var door = await SignInAsync("onedrop", van.Email);
        Assert.Contains("1 parcel collected", await CollectAsync(door, pickup.Id, [morning], [morning]));

        // The van has been: a parcel booked now is listed for tomorrow at once, and the merchant is told
        var evening = await BookAsync(shop.ApiKey);
        var next = Assert.Single(await PickupsAsync(shop.Id), r => r.Status == PickupStatus.Scheduled);
        Assert.Equal(Today.AddDays(1), next.PickupDate);
        var merchant = await SignInAsync("onedrop", shop.Email);
        Assert.Contains("tomorrow's pickup", WebUtility.HtmlDecode(await merchant.PageAsync($"/Merchant/Parcel/{evening}")));
        Assert.Contains("The truck has been here today", WebUtility.HtmlDecode(await merchant.PageAsync("/Merchant/Pickups")));
        Assert.Contains("with tomorrow's pickup", WebUtility.HtmlDecode(await merchant.PageAsync("/Merchant")));
        Assert.Contains($"data-visited=\"{next.PickupPointId}\"", await merchant.PageAsync("/Merchant/NewParcel"));

        // The hub cannot send the van back to the same point today, and the job keeps tomorrow's list as it is
        var refused = WebUtility.HtmlDecode(await hub.SubmitAsync($"/Hub/Pickups?hub={Hub}", $"/Hub/Pickups?handler=Assign&id={next.Id}&hub={Hub}", ("riderId", $"{van.Id}")));
        Assert.Contains("already been to this point today", refused);
        await CatchUpAsync(DateTime.UtcNow);
        Assert.Equal(2, (await PickupsAsync(shop.Id)).Count);
        Assert.Null(Assert.Single(await PickupsAsync(shop.Id), r => r.Status == PickupStatus.Scheduled).RiderId);

        // The visit counts for its own day only: two days on, a parcel booked at the point goes that day again
        Assert.Equal(Today.AddDays(2), await DayForAsync(At(Today.AddDays(2), 10), next.PickupPointId));
    }

    [Fact]
    public async Task A_parcel_booked_while_the_rider_is_at_the_door_is_put_back_on_their_list_before_the_pickup_is_done()
    {
        WebAppFactory.RequireDatabase();
        var shop = await NewMerchantAsync(area: Area);
        var van = await NewRiderAsync(Hub, vehicle: Vehicle.Van);
        var first = await BookAsync(shop.ApiKey);
        var pickup = Assert.Single(await PickupsAsync(shop.Id));
        var hub = await SignInAsync("onedrop", "hub@onedrop.test");
        await hub.SubmitAsync($"/Hub/Pickups?hub={Hub}", $"/Hub/Pickups?handler=Assign&id={pickup.Id}&hub={Hub}", ("riderId", $"{van.Id}"));
        var door = await SignInAsync("onedrop", van.Email);
        Assert.Contains(first, await door.PageAsync("/Rider/Pickups"));

        // The merchant books once more while the rider stands at the door: saving the list as it was drawn is refused
        var late = await BookAsync(shop.ApiKey);
        var refused = WebUtility.HtmlDecode(await CollectAsync(door, pickup.Id, [first], [first]));
        Assert.Contains("booked 1 more parcel while you were here", refused);
        Assert.Equal(PickupStatus.Assigned, Assert.Single(await PickupsAsync(shop.Id)).Status);
        Assert.Equal(ParcelStatus.Pending, (await ParcelAsync(first)).Status);

        // It is on the list now, and the rider takes both: nothing is left for tomorrow
        Assert.Contains(late, await door.PageAsync("/Rider/Pickups"));
        Assert.Contains("2 parcels collected", await CollectAsync(door, pickup.Id, [first, late], [first, late]));
        Assert.Equal(ParcelStatus.PickedUp, (await ParcelAsync(late)).Status);
        Assert.Equal(PickupStatus.Completed, Assert.Single(await PickupsAsync(shop.Id)).Status);
    }

    [Fact]
    public async Task A_parcel_the_rider_leaves_at_the_door_goes_with_tomorrow_s_pickup()
    {
        WebAppFactory.RequireDatabase();
        var shop = await NewMerchantAsync(area: Area);
        var van = await NewRiderAsync(Hub, vehicle: Vehicle.Van);
        var taken = await BookAsync(shop.ApiKey);
        var left = await BookAsync(shop.ApiKey);
        var pickup = Assert.Single(await PickupsAsync(shop.Id));
        var hub = await SignInAsync("onedrop", "hub@onedrop.test");
        await hub.SubmitAsync($"/Hub/Pickups?hub={Hub}", $"/Hub/Pickups?handler=Assign&id={pickup.Id}&hub={Hub}", ("riderId", $"{van.Id}"));
        var door = await SignInAsync("onedrop", van.Email);

        Assert.Contains("1 parcel collected", await CollectAsync(door, pickup.Id, [taken], [taken, left]));
        Assert.Equal(ParcelStatus.Pending, (await ParcelAsync(left)).Status);
        var next = Assert.Single(await PickupsAsync(shop.Id), r => r.Status == PickupStatus.Scheduled);
        Assert.Equal(Today.AddDays(1), next.PickupDate);

        // A pickup dated tomorrow is not one the rider can be given today
        Assert.Contains("already been to this point today", WebUtility.HtmlDecode(await hub.SubmitAsync($"/Hub/Pickups?hub={Hub}", $"/Hub/Pickups?handler=Assign&id={next.Id}&hub={Hub}", ("riderId", $"{van.Id}"))));
    }

    [Fact]
    public async Task A_missed_pickup_is_carried_to_today_and_the_rider_collects_everything_waiting()
    {
        WebAppFactory.RequireDatabase();
        var tenant = await TenantAsync("onedrop");
        var shop = await NewMerchantAsync(area: Area);
        var rider = await NewRiderAsync(Hub, vehicle: Vehicle.Van);
        var zone = tenant.Zone();
        DateTime When(DateOnly day, int hour) => TimeZoneInfo.ConvertTimeToUtc(day.ToDateTime(new TimeOnly(hour, 0)), zone);

        // Booked yesterday morning, yesterday evening, and today in the afternoon
        var morning = await BookAsync(shop.ApiKey);
        var evening = await BookAsync(shop.ApiKey);
        var late = await BookAsync(shop.ApiKey);
        await QueryAsync("onedrop", async db =>
        {
            await db.Parcels.Where(p => p.TrackingCode == morning).ExecuteUpdateAsync(s => s.SetProperty(p => p.Created, When(Today.AddDays(-1), 10)), Cancel);
            await db.Parcels.Where(p => p.TrackingCode == evening).ExecuteUpdateAsync(s => s.SetProperty(p => p.Created, When(Today.AddDays(-1), 16)), Cancel);
            await db.Parcels.Where(p => p.TrackingCode == late).ExecuteUpdateAsync(s => s.SetProperty(p => p.Created, When(Today, 16)), Cancel);

            // Yesterday's pickup was never made, and the one booking scheduled is set aside
            await db.PickupRequests.Where(r => r.MerchantId == shop.Id).ExecuteUpdateAsync(s => s.SetProperty(r => r.Status, PickupStatus.Cancelled), Cancel);
            var point = await db.PickupPoints.SingleAsync(p => p.MerchantId == shop.Id, Cancel);
            var hubId = await db.Hubs.Where(h => h.Code == Hub).Select(h => h.Id).SingleAsync(Cancel);
            db.PickupRequests.Add(PickupRequest.Schedule(shop.Id, point.Id, hubId, Today.AddDays(-1)));

            return await db.SaveChangesAsync(Cancel);
        });

        // The list is brought up to date this morning: yesterday's pickup goes on today's list
        await CatchUpAsync(When(Today, 10));
        var pickup = Assert.Single(await PickupsAsync(shop.Id), r => r.Status != PickupStatus.Cancelled);
        Assert.Equal(Today, pickup.PickupDate);

        var hub = await SignInAsync("onedrop", "hub@onedrop.test");
        Assert.Contains(shop.Name, await hub.PageAsync($"/Hub/Pickups?hub={Hub}"));
        Assert.Contains("Rider assigned", await hub.SubmitAsync($"/Hub/Pickups?hub={Hub}", $"/Hub/Pickups?handler=Assign&id={pickup.Id}&hub={Hub}", ("riderId", $"{rider.Id}")));
        var door = await SignInAsync("onedrop", rider.Email);
        var list = await door.PageAsync("/Rider/Pickups");
        Assert.Contains(morning, list);
        Assert.Contains(evening, list);
        Assert.Contains(late, list);

        // No cut-off: the parcel booked this afternoon goes with the other two
        var done = await CollectAsync(door, pickup.Id, [morning, evening, late], [morning, evening, late]);
        Assert.Contains("3 parcels collected", done);
        Assert.Equal(ParcelStatus.PickedUp, (await ParcelAsync(late)).Status);
    }

    [Fact]
    public async Task A_pickup_with_nothing_left_to_collect_is_called_off()
    {
        WebAppFactory.RequireDatabase();
        var shop = await NewMerchantAsync(area: Area);
        var code = await BookAsync(shop.ApiKey);
        await CatchUpAsync(DateTime.UtcNow);
        Assert.Equal(PickupStatus.Scheduled, await StatusAsync(shop.Id));

        var cancelled = await Factory.ClientFor(shop.ApiKey).PostAsync($"/api/v1/parcels/{code}/cancel", null, Cancel);
        Assert.Equal(HttpStatusCode.NoContent, cancelled.StatusCode);
        await CatchUpAsync(DateTime.UtcNow);
        Assert.Equal(PickupStatus.Cancelled, await StatusAsync(shop.Id));
    }

    [Fact]
    public async Task Only_a_pickup_van_is_sent_to_a_merchant_and_carries_what_it_collects_until_the_hub_scans_it_in()
    {
        WebAppFactory.RequireDatabase();
        var shop = await NewMerchantAsync(area: Area);
        var bike = await NewRiderAsync(Hub, vehicle: Vehicle.Bicycle);
        var motorbike = await NewRiderAsync(Hub, vehicle: Vehicle.Motorbike);
        var van = await NewRiderAsync(Hub, vehicle: Vehicle.Van);
        var code = await BookAsync(shop.ApiKey);
        var pickup = await QueryAsync("onedrop", db => db.PickupRequests.AsNoTracking().SingleAsync(r => r.MerchantId == shop.Id && r.Status == PickupStatus.Scheduled, Cancel));
        var hub = await SignInAsync("onedrop", "hub@onedrop.test");
        var url = $"/Hub/Pickups?handler=Assign&id={pickup.Id}&hub={Hub}";

        foreach (var rider in new[] { bike, motorbike })
        {
            Assert.Contains("does not collect from merchants", WebUtility.HtmlDecode(await hub.SubmitAsync($"/Hub/Pickups?hub={Hub}", url, ("riderId", $"{rider.Id}"))));
        }

        Assert.Null((await QueryAsync("onedrop", db => db.PickupRequests.AsNoTracking().SingleAsync(r => r.Id == pickup.Id, Cancel))).RiderId);

        // The page offers the van, not the bicycle or the motorbike
        var page = await hub.PageAsync($"/Hub/Pickups?hub={Hub}");
        Assert.Contains($"value=\"{van.Id}\"", page);
        Assert.DoesNotContain($"value=\"{bike.Id}\"", page);
        Assert.DoesNotContain($"value=\"{motorbike.Id}\"", page);

        Assert.Contains("Rider assigned", await hub.SubmitAsync($"/Hub/Pickups?hub={Hub}", url, ("riderId", $"{van.Id}")));
        var door = await SignInAsync("onedrop", van.Email);
        Assert.Contains("1 parcel collected", await CollectAsync(door, pickup.Id, [code], [code]));

        // On the van until the hub scans it in
        Assert.Equal(van.Id, (await ParcelAsync(code)).RiderId);
        await hub.SubmitAsync($"/Hub/Scan?hub={Hub}", $"/Hub/Scan?mode=Receive&hub={Hub}", ("Code", code));
        Assert.Null((await ParcelAsync(code)).RiderId);
        Assert.Equal(ParcelStatus.AtHub, (await ParcelAsync(code)).Status);
    }


    [Fact]
    public async Task A_merchant_pins_its_pickup_point_and_the_rider_gets_a_route_to_every_stop()
    {
        WebAppFactory.RequireDatabase();
        var shop = await NewMerchantAsync(area: Area);
        var van = await NewRiderAsync(Hub, vehicle: Vehicle.Van);
        var merchant = await SignInAsync("onedrop", shop.Email);
        var point = await QueryAsync("onedrop", db => db.PickupPoints.AsNoTracking().SingleAsync(p => p.MerchantId == shop.Id, Cancel));

        // Not pinned yet: the merchant's settings say so, and the rider is guided by the address
        Assert.Contains("By address", WebUtility.HtmlDecode(await merchant.PageAsync("/Merchant/Settings")));

        var saved = await merchant.SubmitAsync(
            $"/Merchant/PickupPoint/{point.Id}",
            $"/Merchant/PickupPoint/{point.Id}",
            ("areaId", $"{point.AreaId}"),
            ("name", point.Name),
            ("address", point.Address),
            ("phone", point.ContactPhone),
            ("latitude", "23.806964"),
            ("longitude", "90.368393"));
        Assert.Contains("Pickup point saved", WebUtility.HtmlDecode(saved));
        Assert.Equal("23.806964,90.368393", (await QueryAsync("onedrop", db => db.PickupPoints.AsNoTracking().SingleAsync(p => p.Id == point.Id, Cancel))).Pin!.Text);

        // The rider sent to collect gets Navigate to the pin and a route through every stop of the round
        await BookAsync(shop.ApiKey);
        var pickup = Assert.Single(await PickupsAsync(shop.Id));
        var hub = await SignInAsync("onedrop", "hub@onedrop.test");
        await hub.SubmitAsync($"/Hub/Pickups?hub={Hub}", $"/Hub/Pickups?handler=Assign&id={pickup.Id}&hub={Hub}", ("riderId", $"{van.Id}"));
        var door = WebUtility.HtmlDecode(await (await SignInAsync("onedrop", van.Email)).PageAsync("/Rider/Pickups"));
        Assert.Contains("destination=23.806964,90.368393", door);
        Assert.Contains("See your pickup round on the map", door);

        // Another merchant cannot open or change this point
        var other = await NewMerchantAsync(area: Area);
        var stranger = await SignInAsync("onedrop", other.Email);
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.GetAsync($"/Merchant/PickupPoint/{point.Id}")).StatusCode);
    }

    /// <summary>
    /// The rider saving a pickup as the page posts it: the codes they ticked, and every code the list offered them, so
    /// that a parcel booked while they stood at the door is caught.
    /// </summary>
    private static Task<string> CollectAsync(Visitor rider, long pickupId, string[] ticked, string[] shown)
    {
        return rider.SubmitAsync(
            "/Rider/Pickups",
            $"/Rider/Pickups?id={pickupId}",
            [.. ticked.Select(code => ("codes", code)), .. shown.Select(code => ("shown", code))]);
    }

    private static DateTime At(DateOnly day, int hour)
    {
        return TimeZoneInfo.ConvertTimeToUtc(day.ToDateTime(new TimeOnly(hour, 0)), TimeZoneInfo.FindSystemTimeZoneById("Asia/Dhaka"));
    }

    private Task<List<PickupRequest>> PickupsAsync(long merchantId)
    {
        return QueryAsync("onedrop", db => db.PickupRequests.AsNoTracking().Where(r => r.MerchantId == merchantId).OrderBy(r => r.Id).ToListAsync(Cancel));
    }

    private async Task CatchUpAsync(DateTime utcNow)
    {
        await using var scope = await ScopeAsync("onedrop");
        await ActivatorUtilities.CreateInstance<PickupSchedule>(scope.ServiceProvider, (TimeProvider)new FakeTimeProvider(utcNow)).CatchUpAsync(Cancel);
    }

    private async Task<DateOnly> DayForAsync(DateTime utcNow, long pointId)
    {
        await using var scope = await ScopeAsync("onedrop");

        return await ActivatorUtilities.CreateInstance<PickupSchedule>(scope.ServiceProvider, (TimeProvider)new FakeTimeProvider(utcNow)).DayForAsync(pointId, Cancel);
    }

    private Task<PickupStatus> StatusAsync(long merchantId)
    {
        return QueryAsync("onedrop", db => db.PickupRequests.Where(r => r.MerchantId == merchantId).Select(r => r.Status).SingleAsync(Cancel));
    }
}
