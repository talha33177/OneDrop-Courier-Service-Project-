using Microsoft.EntityFrameworkCore;
using Domain.Parcels;

namespace Integration.Tests;

/// <summary>
/// Changes that would strand work: a rider stopped or moved while carrying parcels, a pickup point moved to another
/// zone while parcels wait there, and a hold for a day that has already come.
/// </summary>
public class GuardTests(WebAppFactory factory) : AppTests(factory)
{
    [Fact]
    public async Task A_rider_out_with_parcels_is_neither_stopped_nor_moved_and_a_hold_is_for_a_day_still_to_come()
    {
        WebAppFactory.RequireDatabase();
        var shop = await NewMerchantAsync(area: "Uttara Sector 7");
        var deliverer = await NewRiderAsync("UTT");
        var code = await BookAsync(shop.ApiKey, area: "Uttara Sector 4", cod: 500);
        var hub = await SignInAsync("onedrop", "hub@onedrop.test");
        var rider = await SignInAsync("onedrop", deliverer.Email);
        var admin = await SignInAsync("onedrop", "admin@onedrop.test");
        await hub.SubmitAsync("/Hub/Scan?hub=UTT", "/Hub/Scan?mode=Receive&hub=UTT", ("Code", code));
        await hub.SubmitAsync("/Hub/Assign?hub=UTT", "/Hub/Assign?hub=UTT", ("riderId", $"{deliverer.Id}"), ("codes", code));

        Assert.Contains("cannot be stopped yet", await admin.SubmitAsync("/Admin/Riders", $"/Admin/Riders?handler=Active&id={deliverer.Id}&active=false"));
        var (phone, mirpur) = await QueryAsync("onedrop", async db => (
            await db.Riders.Where(r => r.Id == deliverer.Id).Select(r => r.Phone).SingleAsync(Cancel),
            await db.Hubs.Where(h => h.Code == "MIR").Select(h => h.Id).SingleAsync(Cancel)));
        Assert.Contains(
            "cannot be moved to another hub yet",
            await admin.SubmitAsync("/Admin/Riders", "/Admin/Riders?handler=Edit", ("id", $"{deliverer.Id}"), ("name", "Test rider"), ("phone", phone), ("hubId", $"{mirpur}"), ("vehicle", "Motorbike")));
        Assert.False(await QueryAsync("onedrop", db => db.Riders.Where(r => r.Id == deliverer.Id).Select(r => r.Archived).SingleAsync(Cancel)));

        var yesterday = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, TimeZoneInfo.FindSystemTimeZoneById("Asia/Dhaka"))).AddDays(-1);
        Assert.Contains(
            "must be after today",
            await rider.SubmitAsync($"/Rider/Delivery/{code}", $"/Rider/Delivery/{code}?handler=Hold", ("reason", "Customer asked for another day"), ("until", yesterday.ToString("yyyy-MM-dd"))));
        Assert.Equal(ParcelStatus.OutForDelivery, (await ParcelAsync(code)).Status);

        // Delivered and the run closed: now the rider can go
        await rider.SubmitAsync($"/Rider/Delivery/{code}", $"/Rider/Delivery/{code}?handler=Deliver", ("collected", "500"), ("reason", ""));
        var runId = await QueryAsync("onedrop", db => db.DeliveryRuns.Where(r => r.RiderId == deliverer.Id).Select(r => r.Id).SingleAsync(Cancel));
        await hub.SubmitAsync("/Hub/Runs?hub=UTT", $"/Hub/Runs?handler=Close&id={runId}&hub=UTT", ("received", "500"));

        Assert.Contains("Rider stopped", await admin.SubmitAsync("/Admin/Riders", $"/Admin/Riders?handler=Active&id={deliverer.Id}&active=false"));
    }

    [Fact]
    public async Task A_pickup_point_with_parcels_waiting_does_not_move_to_another_zone()
    {
        WebAppFactory.RequireDatabase();
        var shop = await NewMerchantAsync(area: "Mirpur 10");
        var merchant = await SignInAsync("onedrop", shop.Email);
        var (pointId, gulshan) = await QueryAsync("onedrop", async db => (
            await db.PickupPoints.Where(p => p.MerchantId == shop.Id).Select(p => p.Id).SingleAsync(Cancel),
            await db.Areas.Where(a => a.Name == "Gulshan 2").Select(a => a.Id).SingleAsync(Cancel)));
        (string, string)[] moved = [("areaId", $"{gulshan}"), ("name", "Shop"), ("address", "Road 5, Gulshan"), ("phone", NewPhone())];
        var point = $"/Merchant/PickupPoint/{pointId}";

        await BookAsync(shop.ApiKey, area: "Banani");
        Assert.Contains("Parcels or a pickup are waiting at this point", await merchant.SubmitAsync(point, point, moved));

        var code = await QueryAsync("onedrop", db => db.Parcels.Where(p => p.MerchantId == shop.Id).Select(p => p.TrackingCode).SingleAsync(Cancel));
        await merchant.SubmitAsync($"/Merchant/Parcel/{code}", $"/Merchant/Parcel/{code}?handler=Cancel", ("reason", "Out of stock"));
        Assert.DoesNotContain("Parcels or a pickup are waiting at this point", await merchant.SubmitAsync(point, point, moved));
        Assert.Equal(gulshan, await QueryAsync("onedrop", db => db.PickupPoints.Where(p => p.Id == pointId).Select(p => p.AreaId).SingleAsync(Cancel)));
    }
}
