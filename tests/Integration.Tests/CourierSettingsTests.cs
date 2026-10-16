using System.Net;

namespace Integration.Tests;

/// <summary>
/// The courier's admin keeping its own settings. It changes the rival courier and puts it back, because the launch
/// courier's attempts and times are what every other test runs under.
/// </summary>
public class CourierSettingsTests(WebAppFactory factory) : AppTests(factory)
{
    [Fact]
    public async Task An_admin_changes_the_courier_s_settings_and_they_apply_at_once_to_that_courier_only()
    {
        WebAppFactory.RequireDatabase();
        var before = await TenantAsync("rival");
        var onedrop = await TenantAsync("onedrop");
        var admin = await SignInAsync("rival", "admin@rival.test");
        var hotline = $"09610-{Random.Shared.Next(100000, 999999)}";

        try
        {
            var saved = await admin.SubmitAsync(
                "/Admin/Settings",
                "/Admin/Settings",
                ("name", before.Name),
                ("supportPhone", hotline),
                ("smsSenderName", "RivalBD"),
                ("maxDeliveryAttempts", "4"),
                ("maxBusinessesPerAccount", "5"),
                ("riderReturnTime", "20:00"),
                ("pickupTime", "14:30"),
                ("stuckAfterDays", "5"));
            Assert.Contains("Settings saved", saved);

            // Read from the cached courier, so this also proves the cache was cleared
            var after = await TenantAsync("rival");
            Assert.Equal(hotline, after.SupportPhone);
            Assert.Equal("RivalBD", after.SmsSenderName);
            Assert.Equal(4, after.MaxDeliveryAttempts);
            Assert.Equal(5, after.MaxBusinessesPerAccount);
            Assert.Equal(new TimeOnly(14, 30), after.PickupTime);
            Assert.Equal(new TimeOnly(20, 0), after.RiderReturnTime);
            Assert.Equal(5, after.StuckAfterDays);
            Assert.Contains(hotline, await admin.PageAsync("/Admin"));

            var untouched = await TenantAsync("onedrop");
            Assert.Equal(onedrop, untouched);

            // A refusal says why and keeps what was typed
            var refused = await admin.SubmitAsync(
                "/Admin/Settings",
                "/Admin/Settings",
                ("name", "Renamed courier"),
                ("supportPhone", hotline),
                ("smsSenderName", "RivalBD"),
                ("maxDeliveryAttempts", "0"),
                ("pickupTime", "13:00"));
            Assert.Contains("A parcel gets between 1 and", refused);
            Assert.Contains("value=\"Renamed courier\"", refused);
            Assert.Equal(before.Name, (await TenantAsync("rival")).Name);
        }
        finally
        {
            await admin.SubmitAsync(
                "/Admin/Settings",
                "/Admin/Settings",
                ("name", before.Name),
                ("supportPhone", before.SupportPhone),
                ("smsSenderName", before.SmsSenderName),
                ("maxDeliveryAttempts", before.MaxDeliveryAttempts.ToString()),
                ("maxBusinessesPerAccount", before.MaxBusinessesPerAccount?.ToString() ?? ""),
                ("riderReturnTime", before.RiderReturnTime?.ToString("HH:mm") ?? ""),
                ("pickupTime", before.PickupTime?.ToString("HH:mm") ?? ""),
                ("stuckAfterDays", before.StuckAfterDays?.ToString() ?? ""));
        }

        Assert.Equal(before, await TenantAsync("rival"));
    }

    [Fact]
    public async Task Only_the_courier_s_admin_opens_its_settings()
    {
        WebAppFactory.RequireDatabase();
        var shop = await NewMerchantAsync();
        var merchant = await SignInAsync("onedrop", shop.Email);
        var hub = await SignInAsync("onedrop", "hub@onedrop.test");

        Assert.NotEqual(HttpStatusCode.OK, (await merchant.GetAsync("/Admin/Settings")).StatusCode);
        Assert.NotEqual(HttpStatusCode.OK, (await hub.GetAsync("/Admin/Settings")).StatusCode);
        Assert.Contains("Courier settings", await (await SignInAsync("onedrop", "admin@onedrop.test")).PageAsync("/Admin/Settings"));
    }
}
