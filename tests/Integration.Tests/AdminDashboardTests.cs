namespace Integration.Tests;

/// <summary>
/// The admin's way around: the dashboard opens on what waits for them and the parcels' day in steps, the guide is one
/// press away, and a finished action offers the next one.
/// </summary>
public class AdminDashboardTests(WebAppFactory factory) : AppTests(factory)
{
    [Fact]
    public async Task The_dashboard_says_what_waits_and_where_each_step_of_the_day_is_done()
    {
        WebAppFactory.RequireDatabase();
        var first = await NewMerchantAsync(approved: false);
        await NewMerchantAsync(approved: false);
        var admin = await SignInAsync("onedrop", "admin@onedrop.test");

        var dashboard = await admin.PageAsync("/Admin");
        Assert.Contains("Waiting for you", dashboard);
        Assert.Matches(@"<a href=""/Admin/Merchants\?status=Pending"">", dashboard);
        Assert.Contains("The parcels' day, across every hub", dashboard);
        foreach (var step in new[] { "Pickups", "Scan", "Assign", "Runs", "Returns" })
        {
            Assert.Contains($"href=\"/Hub/Choose?next={step}\"", dashboard);
        }

        // The test courier is set up, so the setup checklist stays away; the guide is in the menu
        Assert.DoesNotContain("Set up the courier", dashboard);
        Assert.Contains("href=\"/Admin/Guide\"", dashboard);

        // Approving one sign-up offers the next while others wait
        var page = $"/Admin/Merchant/{first.Id}";
        var approved = await admin.SubmitAsync(page, $"{page}?handler=Approve");
        Assert.Contains("Approved: the merchant can book parcels now.", approved);
        Assert.Matches(@"Next sign-up \([0-9]+ waiting\)", approved);
    }
}
