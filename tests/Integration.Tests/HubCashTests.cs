using System.Net;
using Microsoft.EntityFrameworkCore;
using Domain.Payments;

namespace Integration.Tests;

/// <summary>
/// A hub's cash through the pages: a rider's run closed short waits for a manager, the cash desk pays the cash into the
/// bank, and finance confirms or refuses each deposit. Khulna is used by no other test, so its cash is this test's.
/// </summary>
public class HubCashTests(WebAppFactory factory) : AppTests(factory)
{
    [Fact]
    public async Task Cash_handed_in_short_waits_for_a_manager_and_deposits_wait_for_finance()
    {
        WebAppFactory.RequireDatabase();
        var shop = await NewMerchantAsync(area: "Khulna Sadar");
        var deliverer = await NewRiderAsync("KHU");
        var code = await BookAsync(shop.ApiKey, area: "Khulna Sadar", cod: 1000);
        var hub = await SignInAsync("onedrop", "hub@onedrop.test");
        var before = await InHandAsync();
        await hub.SubmitAsync("/Hub/Scan?hub=KHU", "/Hub/Scan?mode=Receive&hub=KHU", ("Code", code));
        await hub.SubmitAsync("/Hub/Assign?hub=KHU", "/Hub/Assign?hub=KHU", ("riderId", $"{deliverer.Id}"), ("codes", code));
        var rider = await SignInAsync("onedrop", deliverer.Email);
        await rider.SubmitAsync($"/Rider/Delivery/{code}", $"/Rider/Delivery/{code}?handler=Deliver", ("collected", "1000"), ("reason", ""));
        var runId = await QueryAsync("onedrop", db => db.DeliveryRuns.Where(r => r.RiderId == deliverer.Id).Select(r => r.Id).SingleAsync(Cancel));

        // Every parcel is recorded but the run is open: the board still has work, and says it is the run to close
        var openRun = await hub.PageAsync("/Hub?hub=KHU");
        Assert.Contains("All recorded: 1 run to close", openRun);
        Assert.Contains("1 run still open", openRun);

        // Closed ৳100 short: the cash at the hub grows by what was handed in, and the shortfall waits for a manager
        Assert.Contains("Run closed ৳100 short", await hub.SubmitAsync("/Hub/Runs?hub=KHU", $"/Hub/Runs?handler=Close&id={runId}&hub=KHU", ("received", "900")));
        Assert.Equal(before + 900, await InHandAsync());
        var cash = await hub.PageAsync("/Hub/Cash?hub=KHU");
        Assert.Contains("Waiting for the hub manager", cash);
        Assert.DoesNotContain("handler=Accept", cash);
        var refused = await hub.PostFormAsync("/Hub/Cash?hub=KHU", $"/Hub/Cash?handler=Accept&id={runId}&hub=KHU", ("note", "Taken from pay"));
        Assert.NotEqual(HttpStatusCode.OK, refused.StatusCode);
        Assert.Contains("Khulna hub: cash and deposits", cash);
        Assert.Contains("Khulna hub holds", cash);
        var admin = await SignInAsync("onedrop", "admin@onedrop.test");
        Assert.Contains("waiting for a hub manager", await admin.PageAsync("/Admin/Cash"));

        // Whoever looks for the cash is told which hub holds it: on the board of every hub, and in the hub chooser
        var board = await admin.PageAsync("/Hub?hub=*");
        Assert.Contains("Cash at the hubs", board);
        Assert.Contains("/Hub/Cash?hub=KHU", WebUtility.HtmlDecode(board));
        var chooser = await admin.PageAsync("/Hub/Choose?next=Cash");
        Assert.Contains("Khulna hub", chooser);
        Assert.Contains("Shortfall accepted", await admin.SubmitAsync("/Hub/Cash?hub=KHU", $"/Hub/Cash?handler=Accept&id={runId}&hub=KHU", ("note", "Taken from Rafiq's pay")));
        var run = await QueryAsync("onedrop", db => db.DeliveryRuns.SingleAsync(r => r.Id == runId, Cancel));
        Assert.Equal("Taken from Rafiq's pay", run.ShortNote);

        // The cash desk pays it in: never more than the hub holds, and one slip once
        var slip = $"KHU-{Guid.NewGuid():N}"[..20];
        Assert.Contains("cannot have paid in", await Deposit(hub, before + 901, slip));
        Assert.Contains("recorded", await Deposit(hub, 900, slip));
        Assert.Contains("already recorded as deposit", await Deposit(hub, 900, slip.ToLowerInvariant()));
        Assert.Equal(before, await InHandAsync());
        var number = await QueryAsync("onedrop", db => db.CashDeposits.Where(d => d.SlipNumber == slip.ToUpperInvariant()).Select(d => d.Number).SingleAsync(Cancel));

        // Finance refuses it, which gives the hub its cash back; recorded again, finance confirms it
        var accounts = await SignInAsync("onedrop", "accounts@onedrop.test");
        Assert.Contains(number, await accounts.PageAsync("/Admin/Cash"));
        Assert.Contains("refused", await accounts.SubmitAsync("/Admin/Cash", "/Admin/Cash?handler=Refuse", ("number", number), ("reason", "Only ৳800 on the statement")));
        Assert.Equal(before + 900, await InHandAsync());
        Assert.Contains("recorded", await Deposit(hub, 900, slip));
        var again = await QueryAsync("onedrop", db => db.CashDeposits.Where(d => d.SlipNumber == slip.ToUpperInvariant() && d.Status == CashDepositStatus.Recorded).Select(d => d.Number).SingleAsync(Cancel));
        Assert.Contains($"Deposit {again} confirmed", await accounts.SubmitAsync("/Admin/Cash", "/Admin/Cash?handler=Confirm", ("number", again)));
        Assert.Equal(CashDepositStatus.Confirmed, await QueryAsync("onedrop", db => db.CashDeposits.Where(d => d.Number == again).Select(d => d.Status).SingleAsync(Cancel)));

        // The cash desk and a hub's staff do not answer for finance; another courier sees none of it
        Assert.NotEqual(HttpStatusCode.OK, (await hub.GetAsync("/Admin/Cash")).StatusCode);
        Assert.DoesNotContain(number, await (await SignInAsync("rival", "admin@rival.test")).PageAsync("/Admin/Cash"));
    }

    private static Task<string> Deposit(Visitor hub, decimal amount, string slip)
    {
        return hub.SubmitAsync(
            "/Hub/Cash?hub=KHU",
            "/Hub/Cash?handler=Deposit&hub=KHU",
            ("amount", amount.ToString("0")),
            ("bank", "Sonali Bank, Khulna"),
            ("slip", slip),
            ("depositedOn", DateTime.UtcNow.AddHours(6).ToString("yyyy-MM-dd")));
    }

    /// <summary>The cash at Khulna: handed in at closing less what was paid in and not refused.</summary>
    private Task<decimal> InHandAsync()
    {
        return QueryAsync("onedrop", async db =>
        {
            var hub = await db.Hubs.Where(h => h.Code == "KHU").Select(h => h.Id).SingleAsync(Cancel);
            var handedIn = await db.DeliveryRuns.Where(r => r.HubId == hub && r.CashReceived != null).SumAsync(r => r.CashReceived, Cancel) ?? 0;
            var paidIn = await db.CashDeposits.Where(d => d.HubId == hub && d.Status != CashDepositStatus.Refused).SumAsync(d => (decimal?)d.Amount, Cancel) ?? 0;

            return handedIn - paidIn;
        });
    }
}
