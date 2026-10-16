using System.Net;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;

namespace Integration.Tests;

/// <summary>
/// Staff work by their job: a hub's own staff only at their hub and only at their desks, head office at every hub but
/// only in the panels its job opens, and a hub manager runs their own hub's people without handing out more than
/// they hold. Uses the seeded demo staff: a receiving clerk and a hub manager at Mirpur, accounts at head office.
/// </summary>
public partial class StaffRolesTests(WebAppFactory factory) : AppTests(factory)
{
    [Fact]
    public async Task A_receiving_clerk_works_only_the_receive_desk_and_only_at_their_hub()
    {
        WebAppFactory.RequireDatabase();
        var clerk = await SignInAsync("onedrop", "receiving@onedrop.test");

        // Their hub opens without choosing; another hub's code is not found, and every hub is never shown
        Assert.Contains("Mirpur", await clerk.PageAsync("/Hub"));
        Assert.Equal(HttpStatusCode.NotFound, (await clerk.GetAsync("/Hub?hub=GUL")).StatusCode);
        Assert.DoesNotContain("All hubs", await clerk.PageAsync("/Hub?hub=*"));

        // The scanner offers only receiving; another desk's mode moves them back to it, and posting one is refused
        var scanner = await clerk.PageAsync("/Hub/Scan");
        Assert.DoesNotContain("Send to another hub", scanner);
        Assert.DoesNotContain("Hand back to merchant", scanner);
        var moved = await clerk.GetAsync("/Hub/Scan?mode=Dispatch");
        Assert.Equal(HttpStatusCode.Redirect, moved.StatusCode);
        Assert.Contains("mode=Receive", moved.Headers.Location!.ToString());
        await RefusedAsync(clerk, "/Hub/Scan", "/Hub/Scan?mode=Dispatch&hub=MIR", ("Code", "OD10000001"));

        // Other desks and the admin panel are shut
        foreach (var page in new[] { "/Hub/Assign?hub=MIR", "/Hub/Runs?hub=MIR", "/Hub/Returns?hub=MIR", "/Hub/Pickups?hub=MIR", "/Admin", "/Admin/Staff", "/Admin/Payouts" })
        {
            await RefusedAsync(clerk, page);
        }

        // A parcel scanned in at Gulshan cannot be weighed from Mirpur; one received at Mirpur can
        var gulshan = await NewMerchantAsync(area: "Gulshan 2");
        var there = await BookAsync(gulshan.ApiKey);
        await (await SignInAsync("onedrop", "hub@onedrop.test")).SubmitAsync("/Hub/Scan?hub=GUL", "/Hub/Scan?mode=Receive&hub=GUL", ("Code", there));
        Assert.Contains("not at your hub", await clerk.SubmitAsync($"/Hub/Parcel/{there}", $"/Hub/Parcel/{there}?handler=Reweigh", ("weightKg", "2")));
        Assert.Equal(500, (await ParcelAsync(there)).WeightGrams);

        var mirpur = await NewMerchantAsync();
        var here = await BookAsync(mirpur.ApiKey);
        await clerk.SubmitAsync("/Hub/Scan?hub=MIR", "/Hub/Scan?mode=Receive&hub=MIR", ("Code", here));
        Assert.Contains("is now charged on", await clerk.SubmitAsync($"/Hub/Parcel/{here}", $"/Hub/Parcel/{here}?handler=Reweigh", ("weightKg", "2")));

        // Sending a parcel back is the returns desk's: no button, and refused when posted
        Assert.DoesNotContain("Return to merchant", await clerk.PageAsync($"/Hub/Parcel/{here}"));
        await RefusedAsync(clerk, $"/Hub/Parcel/{here}", $"/Hub/Parcel/{here}?handler=Return", ("reason", "Wrong address"));
    }

    [Fact]
    public async Task A_hub_manager_adds_people_to_their_own_hub_and_gives_only_what_they_hold()
    {
        WebAppFactory.RequireDatabase();
        var manager = await SignInAsync("onedrop", "mirpur@onedrop.test");
        var mirpur = await HubIdAsync("MIR");
        var gulshan = await HubIdAsync("GUL");

        // They see their hub's people only
        var people = await manager.PageAsync("/Admin/Staff");
        Assert.Contains("receiving@onedrop.test", people);
        Assert.DoesNotContain("accounts@onedrop.test", people);
        Assert.DoesNotContain("admin@onedrop.test", people);

        // Not at another hub, not a head office job, nothing they do not hold, never a super admin
        string Email() => $"{Guid.NewGuid():N}@staff.example";
        Assert.Contains("own hub", await manager.SubmitAsync("/Admin/Staff", "/Admin/Staff?handler=Add", ("Name", "Elsewhere"), ("Email", Email()), ("Job", "CashDesk"), ("HubId", $"{gulshan}")));
        Assert.Contains("own hub", await manager.SubmitAsync("/Admin/Staff", "/Admin/Staff?handler=Add", ("Name", "Office"), ("Email", Email()), ("Job", "Accounts")));
        Assert.Contains("head office", await manager.SubmitAsync("/Admin/Staff", "/Admin/Staff?handler=Add", ("Name", "Money"), ("Email", Email()), ("Job", "CashDesk"), ("HubId", $"{mirpur}"), ("Extra", "Finance")));
        Assert.Contains("Only a super admin", await manager.SubmitAsync("/Admin/Staff", "/Admin/Staff?handler=Add", ("Name", "Boss"), ("Email", Email()), ("Job", "SuperAdmin")));

        // A delivery desk at Mirpur who also covers returns
        var email = Email();
        var added = await manager.SubmitAsync("/Admin/Staff", "/Admin/Staff?handler=Add", ("Name", "Desk"), ("Email", email), ("Job", "DeliveryDesk"), ("HubId", $"{mirpur}"), ("Extra", "Returns"));
        Assert.Contains("Their first password", added);
        var desk = Visit("onedrop", await SignInCookieAsync("onedrop", email, FirstPassword().Match(added).Groups[1].Value));
        Assert.Equal(HttpStatusCode.OK, (await desk.GetAsync("/Hub/Assign?hub=MIR")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await desk.GetAsync("/Hub/Returns?hub=MIR")).StatusCode);
        await RefusedAsync(desk, "/Hub/Runs?hub=MIR");
        await RefusedAsync(desk, "/Admin/Staff");

        // Someone of head office or of another hub is not found from Mirpur
        var accounts = await UserIdAsync("accounts@onedrop.test");
        Assert.Equal(HttpStatusCode.NotFound, (await manager.PostFormAsync("/Admin/Staff", $"/Admin/Staff?handler=Stop&id={accounts}")).StatusCode);
    }

    [Fact]
    public async Task Head_office_staff_open_only_the_panels_their_job_opens()
    {
        WebAppFactory.RequireDatabase();
        var accounts = await SignInAsync("onedrop", "accounts@onedrop.test");

        // Accounts starts on the payouts, may read a merchant, and changes no merchant account
        var home = await accounts.GetAsync("/");
        Assert.Equal("/Admin/Payouts", home.Headers.Location!.ToString());
        var menu = await accounts.PageAsync("/Admin/Payouts");
        Assert.Contains("Merchant payouts", menu);
        Assert.DoesNotContain("Delivery rates", menu);
        Assert.Equal(HttpStatusCode.OK, (await accounts.GetAsync("/Admin/Merchants")).StatusCode);
        var shop = await NewMerchantAsync(approved: false);
        await RefusedAsync(accounts, $"/Admin/Merchant/{shop.Id}", $"/Admin/Merchant/{shop.Id}?handler=Approve");
        foreach (var page in new[] { "/Admin", "/Admin/Coverage", "/Admin/Staff", "/Hub/Scan?hub=MIR", "/Hub/Assign?hub=MIR" })
        {
            await RefusedAsync(accounts, page);
        }

        // A super admin makes an operations manager who also keeps the money: they start on the dashboard over
        // every hub, and see payouts too
        var admin = await SignInAsync("onedrop", "admin@onedrop.test");
        var email = $"{Guid.NewGuid():N}@staff.example";
        var added = await admin.SubmitAsync("/Admin/Staff", "/Admin/Staff?handler=Add", ("Name", "Ops"), ("Email", email), ("Job", "OperationsManager"), ("Extra", "Finance"));
        var ops = Visit("onedrop", await SignInCookieAsync("onedrop", email, FirstPassword().Match(added).Groups[1].Value));
        Assert.Equal("/Admin", (await ops.GetAsync("/")).Headers.Location!.ToString());
        Assert.Equal(HttpStatusCode.OK, (await ops.GetAsync("/Admin")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await ops.GetAsync("/Admin/Payouts")).StatusCode);
        Assert.Contains("All hubs", await ops.PageAsync("/Hub"));
        await RefusedAsync(ops, "/Admin/Settings");

        // A job at one hub is not saved without its hub; the reason shows in the row being edited
        var opsId = await UserIdAsync(email);
        var noHub = await admin.SubmitAsync("/Admin/Staff", $"/Admin/Staff?handler=Change&id={opsId}", ("Name", "Ops"), ("Job", "Receiving"));
        Assert.Contains("Choose the hub", noHub);
        Assert.Contains($"handler=Change&id={opsId}", noHub);
        Assert.Equal(HttpStatusCode.OK, (await ops.GetAsync("/Admin")).StatusCode);
    }

    /// <summary>The page is refused: sent to the access denied page, or not found.</summary>
    private static async Task RefusedAsync(Visitor visitor, string url)
    {
        var response = await visitor.GetAsync(url);
        Assert.True(
            response.StatusCode == HttpStatusCode.NotFound || response.Headers.Location?.ToString().Contains("AccessDenied") == true,
            $"{url} answered {(int)response.StatusCode} {response.Headers.Location}");
    }

    /// <summary>The button is refused: posting it sends the person to the access denied page.</summary>
    private static async Task RefusedAsync(Visitor visitor, string formPage, string url, params (string Name, string Value)[] fields)
    {
        var response = await visitor.PostFormAsync(formPage, url, fields);
        Assert.True(response.Headers.Location?.ToString().Contains("AccessDenied") == true, $"{url} answered {(int)response.StatusCode} {response.Headers.Location}");
    }

    private Task<long> HubIdAsync(string code)
    {
        return QueryAsync("onedrop", db => db.Hubs.Where(h => h.Code == code).Select(h => h.Id).SingleAsync(Cancel));
    }

    private Task<long> UserIdAsync(string email)
    {
        return QueryAsync("onedrop", db => db.Users.Where(u => u.Email == email).Select(u => u.Id).SingleAsync(Cancel));
    }

    [GeneratedRegex("letter-spacing:1px\">([A-Za-z0-9]+)</code>")]
    private static partial Regex FirstPassword();
}
