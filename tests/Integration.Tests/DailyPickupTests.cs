using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Application.Abstractions;
using Application.Common;
using Application.Delivery.Pickups;
using Domain.Delivery;
using Domain.Parcels;

namespace Integration.Tests;

/// <summary>
/// The courier's one pickup a day: booking puts the pickup point on the list of its day (before the cut-off that day,
/// after it the next), the rider collects only what was booked before the cut-off, and a pickup the day passed without
/// is carried over. The merchant asks for nothing. Merchants here collect in Old Dhaka (Motijheel hub), which no other
/// test uses.
/// </summary>
public class DailyPickupTests(WebAppFactory factory) : AppTests(factory)
{
    private const string Area = "Lalbagh";
    private const string Hub = "MOT";

    [Fact]
    public void A_parcel_booked_before_the_cut_off_goes_that_day_and_one_booked_later_the_next()
    {
        var tenant = new TenantInfo(1, "Courier", "courier", "Asia/Dhaka", "BDT", "Courier", "09610", 3, null, null, new TimeOnly(15, 0), new TimeOnly(15, 30));
        var day = new DateOnly(2026, 10, 9);

        // Dhaka is six hours ahead of UTC: 3:00 pm there is 9:00 UTC
        Assert.Equal(day, tenant.PickupDayFor(new DateTime(2026, 10, 9, 8, 59, 59, DateTimeKind.Utc)));
        Assert.Equal(day.AddDays(1), tenant.PickupDayFor(new DateTime(2026, 10, 9, 9, 0, 0, DateTimeKind.Utc)));
        Assert.Equal(day.AddDays(1), tenant.PickupDayFor(new DateTime(2026, 10, 9, 18, 30, 0, DateTimeKind.Utc)));
        Assert.Equal(new DateTime(2026, 10, 9, 9, 0, 0, DateTimeKind.Utc), tenant.PickupCutoffUtc(day));

        var noCutoff = tenant with { PickupCutoff = null };
        Assert.Equal(day, noCutoff.PickupDayFor(new DateTime(2026, 10, 9, 17, 0, 0, DateTimeKind.Utc)));
        Assert.Equal(new DateTime(2026, 10, 9, 18, 0, 0, DateTimeKind.Utc), noCutoff.PickupCutoffUtc(day));
    }

    [Fact]
    public async Task Booking_puts_the_point_on_its_days_list_once_and_the_merchant_asks_for_nothing()
    {
        WebAppFactory.RequireDatabase();
        var shop = await NewMerchantAsync(area: Area);
        var tenant = await TenantAsync("onedrop");
        var first = await BookAsync(shop.ApiKey);
        await BookAsync(shop.ApiKey);

        var created = (await ParcelAsync(first)).Created;
        var pickups = await QueryAsync("onedrop", db => db.PickupRequests.AsNoTracking().Where(r => r.MerchantId == shop.Id).ToListAsync(Cancel));
        var pickup = Assert.Single(pickups);
        Assert.Equal(tenant.PickupDayFor(created), pickup.PickupDate);
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
    public async Task The_rider_collects_only_what_was_booked_before_the_cut_off_and_a_missed_pickup_is_carried_over()
    {
        WebAppFactory.RequireDatabase();
        var tenant = await TenantAsync("onedrop");
        var shop = await NewMerchantAsync(area: Area);
        var rider = await NewRiderAsync(Hub);
        var zone = tenant.Zone();
        DateTime At(DateOnly day, int hour) => TimeZoneInfo.ConvertTimeToUtc(day.ToDateTime(new TimeOnly(hour, 0)), zone);

        // Booked yesterday morning, yesterday after the cut-off, and today after the cut-off
        var morning = await BookAsync(shop.ApiKey);
        var evening = await BookAsync(shop.ApiKey);
        var late = await BookAsync(shop.ApiKey);
        await QueryAsync("onedrop", async db =>
        {
            await db.Parcels.Where(p => p.TrackingCode == morning).ExecuteUpdateAsync(s => s.SetProperty(p => p.Created, At(Today.AddDays(-1), 10)), Cancel);
            await db.Parcels.Where(p => p.TrackingCode == evening).ExecuteUpdateAsync(s => s.SetProperty(p => p.Created, At(Today.AddDays(-1), 16)), Cancel);
            await db.Parcels.Where(p => p.TrackingCode == late).ExecuteUpdateAsync(s => s.SetProperty(p => p.Created, At(Today, 16)), Cancel);

            // Yesterday's pickup was never made, and the one booking scheduled is set aside
            await db.PickupRequests.Where(r => r.MerchantId == shop.Id).ExecuteUpdateAsync(s => s.SetProperty(r => r.Status, PickupStatus.Cancelled), Cancel);
            var point = await db.PickupPoints.SingleAsync(p => p.MerchantId == shop.Id, Cancel);
            var hubId = await db.Hubs.Where(h => h.Code == Hub).Select(h => h.Id).SingleAsync(Cancel);
            db.PickupRequests.Add(PickupRequest.Schedule(shop.Id, point.Id, hubId, Today.AddDays(-1)));

            return await db.SaveChangesAsync(Cancel);
        });

        // The list is brought up to date this morning: yesterday's pickup goes on today's list
        await CatchUpAsync(At(Today, 10));
        var pickup = await QueryAsync("onedrop", db => db.PickupRequests.AsNoTracking().SingleAsync(r => r.MerchantId == shop.Id && r.Status != PickupStatus.Cancelled, Cancel));
        Assert.Equal(Today, pickup.PickupDate);

        var hub = await SignInAsync("onedrop", "hub@onedrop.test");
        Assert.Contains(shop.Name, await hub.PageAsync($"/Hub/Pickups?hub={Hub}"));
        Assert.Contains("Rider assigned", await hub.SubmitAsync($"/Hub/Pickups?hub={Hub}", $"/Hub/Pickups?handler=Assign&id={pickup.Id}&hub={Hub}", ("riderId", $"{rider.Id}")));
        var door = await SignInAsync("onedrop", rider.Email);
        var list = await door.PageAsync("/Rider/Pickups");
        Assert.Contains(morning, list);
        Assert.Contains(evening, list);
        Assert.DoesNotContain(late, list);

        // Ticking the late one too collects only the two that go with today's pickup
        var done = await door.SubmitAsync("/Rider/Pickups", $"/Rider/Pickups?id={pickup.Id}", ("codes", morning), ("codes", evening), ("codes", late));
        Assert.Contains("2 parcels collected", done);
        Assert.Equal(ParcelStatus.PickedUp, (await ParcelAsync(evening)).Status);
        Assert.Equal(ParcelStatus.Pending, (await ParcelAsync(late)).Status);
        Assert.Equal(Today.AddDays(1), tenant.PickupDayFor((await ParcelAsync(late)).Created));
    }

    [Fact]
    public async Task A_pickup_with_nothing_left_to_collect_is_called_off_once_the_list_closes()
    {
        WebAppFactory.RequireDatabase();
        var tenant = await TenantAsync("onedrop");
        var shop = await NewMerchantAsync(area: Area);
        var code = await BookAsync(shop.ApiKey);
        var cancelled = await Factory.ClientFor(shop.ApiKey).PostAsync($"/api/v1/parcels/{code}/cancel", null, Cancel);
        Assert.Equal(HttpStatusCode.NoContent, cancelled.StatusCode);
        var day = await QueryAsync("onedrop", db => db.PickupRequests.Where(r => r.MerchantId == shop.Id).Select(r => r.PickupDate).SingleAsync(Cancel));

        // Before the cut-off the merchant may still book; after it the pickup is called off
        if (day == Today)
        {
            await CatchUpAsync(TimeZoneInfo.ConvertTimeToUtc(day.ToDateTime(new TimeOnly(9, 0)), tenant.Zone()));
            Assert.Equal(PickupStatus.Scheduled, await StatusAsync(shop.Id));
            await CatchUpAsync(TimeZoneInfo.ConvertTimeToUtc(day.ToDateTime(new TimeOnly(16, 0)), tenant.Zone()));
            Assert.Equal(PickupStatus.Cancelled, await StatusAsync(shop.Id));
        }
        else
        {
            // Booked after today's cut-off: the pickup is tomorrow's, and stays on its list until then
            await CatchUpAsync(DateTime.UtcNow);
            Assert.Equal(PickupStatus.Scheduled, await StatusAsync(shop.Id));
        }
    }

    private async Task CatchUpAsync(DateTime utcNow)
    {
        await using var scope = await ScopeAsync("onedrop");
        await ActivatorUtilities.CreateInstance<PickupSchedule>(scope.ServiceProvider, (TimeProvider)new FakeTimeProvider(utcNow)).CatchUpAsync(Cancel);
    }

    private Task<PickupStatus> StatusAsync(long merchantId)
    {
        return QueryAsync("onedrop", db => db.PickupRequests.Where(r => r.MerchantId == merchantId).Select(r => r.Status).SingleAsync(Cancel));
    }
}
