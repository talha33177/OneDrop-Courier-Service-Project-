using System.Net;
using Microsoft.EntityFrameworkCore;

namespace Integration.Tests;

/// <summary>A page per rider for the admin: their runs, the doors they recorded and the cash they kept back.</summary>
public class AdminRiderTests(WebAppFactory factory) : AppTests(factory)
{
    [Fact]
    public async Task The_admin_reads_a_rider_s_runs_doors_and_the_cash_short_at_closing()
    {
        WebAppFactory.RequireDatabase();
        var shop = await NewMerchantAsync();
        var rider = await NewRiderAsync("GUL");
        var delivered = await BookAsync(shop.ApiKey, area: "Gulshan 2", cod: 1250);
        var refused = await BookAsync(shop.ApiKey, area: "Banani", cod: 500);
        var hub = await SignInAsync("onedrop", "hub@onedrop.test");
        foreach (var code in new[] { delivered, refused })
        {
            await hub.SubmitAsync("/Hub/Scan?hub=GUL", "/Hub/Scan?mode=Receive&hub=GUL", ("Code", code));
        }

        await hub.SubmitAsync("/Hub/Assign?hub=GUL", "/Hub/Assign?hub=GUL", ("riderId", $"{rider.Id}"), ("codes", delivered), ("codes", refused));
        var admin = await SignInAsync("onedrop", "admin@onedrop.test");

        // Out on the run: both parcels are with the rider, nothing recorded yet
        var carrying = await admin.PageAsync($"/Admin/Rider/{rider.Id}");
        Assert.Contains("1 run, 1 still open", carrying);
        Assert.Contains("No door recorded yet", carrying);
        Assert.Contains("Run sheet", carrying);

        var door = await SignInAsync("onedrop", rider.Email);
        await door.SubmitAsync($"/Rider/Delivery/{delivered}", $"/Rider/Delivery/{delivered}?handler=Deliver", ("collected", "1250"), ("reason", ""));
        await door.SubmitAsync($"/Rider/Delivery/{refused}", $"/Rider/Delivery/{refused}?handler=Refuse", ("reason", "Customer not interested"));
        var runId = await QueryAsync("onedrop", db => db.DeliveryRuns.Where(r => r.RiderId == rider.Id).Select(r => r.Id).SingleAsync(Cancel));
        Assert.Contains("Run closed", await hub.SubmitAsync("/Hub/Runs?hub=GUL", $"/Hub/Runs?handler=Close&id={runId}&hub=GUL", ("received", "1200")));

        var page = await admin.PageAsync($"/Admin/Rider/{rider.Id}?period=Last7Days");
        Assert.Contains("50% of the doors", page);
        Assert.Contains("৳1,200", page);
        Assert.Contains("handed in at closing", page);
        Assert.Contains("Customer not interested", page);
        Assert.Contains(delivered, page);
        Assert.Contains("৳50", page);
        Assert.DoesNotContain("still open", page);

        // A period that cannot be read says why and shows the last 30 days
        var backwards = await admin.PageAsync($"/Admin/Rider/{rider.Id}?period=Custom&from={Today:yyyy-MM-dd}&to={Today.AddDays(-3):yyyy-MM-dd}");
        Assert.Contains("alert-error", backwards);
        Assert.Contains(delivered, backwards);

        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync("/Admin/Rider/999999999")).StatusCode);
    }
}
