using System.Net;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Domain.Parcels;
using Infrastructure.Email;

namespace Integration.Tests;

/// <summary>
/// The super admin ends accounts: a stopped rider's login is shut, a rider who never worked can be deleted, a merchant
/// is terminated (once settled, with an email) and brought back, or, if it never did anything, deleted; staff who never
/// worked are deleted, and the super admin stops a merchant's moderators. Everyone else is refused.
/// </summary>
[Collection("Emails")]
public partial class AccountRemovalTests(WebAppFactory factory) : AppTests(factory)
{
    private EmailLog Log => Factory.Services.GetRequiredService<EmailLog>();

    [Fact]
    public async Task Stopping_a_rider_shuts_their_login_and_only_a_rider_who_never_worked_is_deleted()
    {
        WebAppFactory.RequireDatabase();
        var idle = await NewRiderAsync("UTT");
        var admin = await SignInAsync("onedrop", "admin@onedrop.test");

        Assert.Contains("Rider stopped", await admin.SubmitAsync("/Admin/Riders", $"/Admin/Riders?handler=Active&id={idle.Id}&active=false"));
        Assert.Contains("has been stopped", await SignInRefusedAsync(idle.Email));
        Assert.Contains("Rider restarted", await admin.SubmitAsync("/Admin/Riders", $"/Admin/Riders?handler=Active&id={idle.Id}&active=true"));
        Assert.NotEmpty(await SignInCookieAsync("onedrop", idle.Email));

        // Never worked: erased with the login
        Assert.Contains("Rider deleted", await admin.SubmitAsync("/Admin/Riders", $"/Admin/Riders?handler=Delete&id={idle.Id}"));
        Assert.False(await QueryAsync("onedrop", db => db.Riders.AnyAsync(r => r.Id == idle.Id, Cancel)));
        Assert.False(await QueryAsync("onedrop", db => db.Users.AnyAsync(u => u.Email == idle.Email, Cancel)));

        // Has delivered: stays on record
        var shop = await NewMerchantAsync(area: "Uttara Sector 7");
        var worked = await NewRiderAsync("UTT");
        var code = await BookAsync(shop.ApiKey, area: "Uttara Sector 4", cod: 500);
        var hub = await SignInAsync("onedrop", "hub@onedrop.test");
        var rider = await SignInAsync("onedrop", worked.Email);
        await hub.SubmitAsync("/Hub/Scan?hub=UTT", "/Hub/Scan?mode=Receive&hub=UTT", ("Code", code));
        await hub.SubmitAsync("/Hub/Assign?hub=UTT", "/Hub/Assign?hub=UTT", ("riderId", $"{worked.Id}"), ("codes", code));
        await rider.SubmitAsync($"/Rider/Delivery/{code}", $"/Rider/Delivery/{code}?handler=Deliver", ("collected", "500"), ("reason", ""));
        var runId = await QueryAsync("onedrop", db => db.DeliveryRuns.Where(r => r.RiderId == worked.Id).Select(r => r.Id).SingleAsync(Cancel));
        await hub.SubmitAsync("/Hub/Runs?hub=UTT", $"/Hub/Runs?handler=Close&id={runId}&hub=UTT", ("received", "500"));

        Assert.Contains("has worked here", await admin.SubmitAsync("/Admin/Riders", $"/Admin/Riders?handler=Delete&id={worked.Id}"));
        Assert.True(await QueryAsync("onedrop", db => db.Riders.AnyAsync(r => r.Id == worked.Id, Cancel)));
    }

    [Fact]
    public async Task A_merchant_that_never_did_anything_is_deleted_with_its_logins_and_one_with_history_is_not()
    {
        WebAppFactory.RequireDatabase();
        var empty = await NewMerchantAsync();
        var busy = await NewMerchantAsync();
        await BookAsync(busy.ApiKey);
        var admin = await SignInAsync("onedrop", "admin@onedrop.test");

        Assert.Contains("cannot be deleted", await admin.SubmitAsync($"/Admin/Merchant/{busy.Id}", $"/Admin/Merchant/{busy.Id}?handler=Delete"));
        Assert.True(await QueryAsync("onedrop", db => db.Merchants.AnyAsync(m => m.Id == busy.Id, Cancel)));

        Assert.Contains("Merchant deleted", await admin.SubmitAsync($"/Admin/Merchant/{empty.Id}", $"/Admin/Merchant/{empty.Id}?handler=Delete"));
        Assert.False(await QueryAsync("onedrop", db => db.Merchants.AnyAsync(m => m.Id == empty.Id, Cancel)));
        Assert.False(await QueryAsync("onedrop", db => db.MerchantApiKeys.AnyAsync(k => k.MerchantId == empty.Id, Cancel)));
        Assert.False(await QueryAsync("onedrop", db => db.PickupPoints.AnyAsync(p => p.MerchantId == empty.Id, Cancel)));
        Assert.False(await QueryAsync("onedrop", db => db.Users.AnyAsync(u => u.Email == empty.Email, Cancel)));
    }

    [Fact]
    public async Task A_merchant_is_terminated_once_settled_and_then_cannot_sign_in_book_or_use_its_key()
    {
        WebAppFactory.RequireDatabase();
        await ClearEmailBacklogAsync();
        var shop = await NewMerchantAsync();
        var waiting = await BookAsync(shop.ApiKey);
        var admin = await SignInAsync("onedrop", "admin@onedrop.test");
        var page = $"/Admin/Merchant/{shop.Id}";

        // Money owed to the merchant, then by it: neither can be left behind
        await admin.SubmitAsync(page, $"{page}?handler=Adjust", ("direction", "credit"), ("amount", "100"), ("note", "Compensation"));
        Assert.Contains("still owes it ৳100", await admin.SubmitAsync(page, $"{page}?handler=Terminate"));
        await admin.SubmitAsync(page, $"{page}?handler=Adjust", ("direction", "charge"), ("amount", "100"), ("note", "Settled in cash"));

        Assert.Contains("Terminated", await admin.SubmitAsync(page, $"{page}?handler=Terminate"));
        Assert.Contains("terminated", await admin.PageAsync(page));
        Assert.Contains("has been terminated already", await admin.SubmitAsync(page, $"{page}?handler=Terminate"));

        // A parcel that had not been picked up is called off; the account's door is shut
        Assert.Equal(ParcelStatus.Cancelled, (await ParcelAsync(waiting)).Status);
        Assert.True(await QueryAsync("onedrop", db => db.Merchants.Where(m => m.Id == shop.Id).Select(m => m.Archived).SingleAsync(Cancel)));
        Assert.Equal(HttpStatusCode.Unauthorized, (await Factory.ClientFor(shop.ApiKey).GetAsync($"/api/v1/parcels/{waiting}", Cancel)).StatusCode);

        // The owner still signs in to read the history, under a warning, and can do nothing else
        var owner = await SignInAsync("onedrop", shop.Email);
        var home = await owner.PageAsync("/Merchant");
        Assert.Contains("This account was terminated by", home);
        Assert.DoesNotContain("Your account is suspended", home);
        Assert.DoesNotContain("Get started", home);
        Assert.Contains(waiting, await owner.PageAsync("/Merchant/Parcels?tab=Cancelled"));
        Assert.Contains("terminated", await owner.PageAsync($"/Merchant/Parcel/{waiting}"));
        Assert.Equal(HttpStatusCode.OK, (await owner.GetAsync("/Merchant/Payments")).StatusCode);
        // The pages that book or change things still open, under the banner, so they can be seen disabled
        foreach (var shown in new[] { "/Merchant/NewParcel", "/Merchant/BulkUpload", "/Merchant/Settings", "/Merchant/ApiKeys", "/Merchant/Moderators" })
        {
            var html = await owner.PageAsync(shown);
            Assert.Contains("This account was terminated by", html);
            Assert.Contains("is-readonly", html);
        }

        Assert.DoesNotContain("not active yet", await owner.PageAsync("/Merchant/NewParcel"));

        // A post that gets past the disabled form is refused and sent back to the same page
        var edit = await owner.PostFormAsync("/Merchant/Settings", "/Merchant/Settings?handler=Profile", ("name", "Renamed while terminated"));
        Assert.Equal(HttpStatusCode.Redirect, edit.StatusCode);
        Assert.Equal("/Merchant/Settings?handler=Profile", edit.Headers.Location?.OriginalString);
        var cancel = await owner.PostFormAsync($"/Merchant/Parcel/{waiting}", $"/Merchant/Parcel/{waiting}?handler=Cancel");
        Assert.Equal($"/Merchant/Parcel/{waiting}?handler=Cancel", cancel.Headers.Location?.OriginalString);
        Assert.NotEqual("Renamed while terminated", await QueryAsync("onedrop", db => db.Merchants.Where(m => m.Id == shop.Id).Select(m => m.Name).SingleAsync(Cancel)));

        // It leaves the list of merchants but its record stays
        Assert.DoesNotContain($"/Admin/Merchant/{shop.Id}\"", await admin.PageAsync($"/Admin/Merchants?search={Uri.EscapeDataString(shop.Name)}"));
        Assert.True(await QueryAsync("onedrop", db => db.Parcels.AnyAsync(p => p.MerchantId == shop.Id, Cancel)));

        // The merchant is told, once; the account shows on the Terminated tab
        await SendEmailsAsync("onedrop");
        Assert.Single(Log.Recent, e => e.To == shop.Email && e.Subject.EndsWith("account has been closed", StringComparison.Ordinal));
        Assert.Contains($"/Admin/Merchant/{shop.Id}\"", await admin.PageAsync($"/Admin/Merchants?terminated=true&search={Uri.EscapeDataString(shop.Name)}"));

        // Brought back: the owner is told and can act again, but the closed API key stays closed
        Assert.Contains("Brought back", await admin.SubmitAsync(page, $"{page}?handler=Restore"));
        Assert.Contains("has not been terminated", await admin.SubmitAsync(page, $"{page}?handler=Restore"));
        var back = await SignInAsync("onedrop", shop.Email);
        Assert.DoesNotContain("This account was terminated by", await back.PageAsync("/Merchant"));
        Assert.Equal(HttpStatusCode.OK, (await back.GetAsync("/Merchant/NewParcel")).StatusCode);
        Assert.Contains($"/Admin/Merchant/{shop.Id}\"", await admin.PageAsync($"/Admin/Merchants?search={Uri.EscapeDataString(shop.Name)}"));
        Assert.Equal(HttpStatusCode.Unauthorized, (await Factory.ClientFor(shop.ApiKey).GetAsync($"/api/v1/parcels/{waiting}", Cancel)).StatusCode);
        await SendEmailsAsync("onedrop");
        Assert.Single(Log.Recent, e => e.To == shop.Email && e.Subject.Contains("has been brought back", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_super_admin_stops_and_lets_back_in_a_merchants_moderator()
    {
        WebAppFactory.RequireDatabase();
        var shop = await NewMerchantAsync();
        var owner = await SignInAsync("onedrop", shop.Email);
        var email = $"{Guid.NewGuid():N}@staff.test";
        var added = WebUtility.HtmlDecode(await (await owner.PostFormAsync(
            "/Merchant/Moderators",
            "/Merchant/Moderators?handler=Add",
            ("Name", "Nusrat Jahan"),
            ("Email", email),
            ("Phone", NewPhone()),
            ("Permissions", "Dashboard"))).Content.ReadAsStringAsync(Cancel));
        var password = FirstPassword().Match(added).Groups[1].Value;
        Assert.NotEmpty(password);
        var id = await QueryAsync("onedrop", db => db.Moderators.Where(m => m.AccountId == shop.Id).Select(m => m.Id).SingleAsync(Cancel));
        var admin = await SignInAsync("onedrop", "admin@onedrop.test");
        var page = $"/Admin/Merchant/{shop.Id}";

        Assert.Contains("They are stopped", await admin.SubmitAsync(page, $"{page}?handler=StopModerator&moderatorId={id}"));
        Assert.Contains("has been stopped", await SignInRefusedAsync(email, password));
        Assert.True(await QueryAsync("onedrop", db => db.Moderators.Where(m => m.Id == id).Select(m => m.Archived).SingleAsync(Cancel)));

        Assert.Contains("sign in again", await admin.SubmitAsync(page, $"{page}?handler=LetModeratorIn&moderatorId={id}"));
        Assert.NotEmpty(await SignInCookieAsync("onedrop", email, password));

        // Accounts staff open the page but may not stop anyone
        var accounts = await SignInAsync("onedrop", "accounts@onedrop.test");
        Assert.NotEqual(HttpStatusCode.OK, (await accounts.PostFormAsync(page, $"{page}?handler=StopModerator&moderatorId={id}")).StatusCode);
        Assert.False(await QueryAsync("onedrop", db => db.Moderators.Where(m => m.Id == id).Select(m => m.Archived).SingleAsync(Cancel)));
    }

    [Fact]
    public async Task The_super_admin_deletes_staff_who_never_worked_and_not_those_who_did_or_themselves()
    {
        WebAppFactory.RequireDatabase();
        var admin = await SignInAsync("onedrop", "admin@onedrop.test");
        var idle = $"{Guid.NewGuid():N}@staff.example";
        var added = await admin.SubmitAsync("/Admin/Staff", "/Admin/Staff?handler=Add", ("Name", "Idle Person"), ("Email", idle), ("Job", "Relief"));
        Assert.Contains("Their first password", added);
        var idleId = await IdAsync(idle);

        Assert.Contains("Deleted", await admin.SubmitAsync("/Admin/Staff", $"/Admin/Staff?handler=Delete&id={idleId}"));
        Assert.False(await QueryAsync("onedrop", db => db.Users.AnyAsync(u => u.Email == idle, Cancel)));
        Assert.False(await QueryAsync("onedrop", db => db.StaffMembers.AnyAsync(s => s.UserId == idleId, Cancel)));

        // Someone who scanned a parcel is the one who did it: they are stopped, not erased
        var busy = $"{Guid.NewGuid():N}@staff.example";
        var password = FirstPassword().Match(await admin.SubmitAsync("/Admin/Staff", "/Admin/Staff?handler=Add", ("Name", "Busy Person"), ("Email", busy), ("Job", "Relief"))).Groups[1].Value;
        var shop = await NewMerchantAsync();
        var code = await BookAsync(shop.ApiKey);
        var worker = Visit("onedrop", await SignInCookieAsync("onedrop", busy, password));
        await worker.SubmitAsync("/Hub/Scan?hub=MIR", "/Hub/Scan?mode=Receive&hub=MIR", ("Code", code));
        var busyId = await IdAsync(busy);
        Assert.Contains("has worked here", await admin.SubmitAsync("/Admin/Staff", $"/Admin/Staff?handler=Delete&id={busyId}"));
        Assert.True(await QueryAsync("onedrop", db => db.Users.AnyAsync(u => u.Id == busyId, Cancel)));
        Assert.True(await QueryAsync("onedrop", db => db.StaffMembers.AnyAsync(s => s.UserId == busyId, Cancel)));

        // Not yourself, and not by anyone but a super admin
        var me = await IdAsync("admin@onedrop.test");
        Assert.Contains("cannot stop yourself", await admin.SubmitAsync("/Admin/Staff", $"/Admin/Staff?handler=Delete&id={me}"));
        var manager = await SignInAsync("onedrop", "mirpur@onedrop.test");
        Assert.NotEqual(HttpStatusCode.OK, (await manager.PostFormAsync("/Admin/Staff", $"/Admin/Staff?handler=Delete&id={busyId}")).StatusCode);
        Assert.True(await QueryAsync("onedrop", db => db.Users.AnyAsync(u => u.Id == busyId, Cancel)));
    }

    private Task<long> IdAsync(string email)
    {
        return QueryAsync("onedrop", db => db.Users.Where(u => u.Email == email).Select(u => u.Id).SingleAsync(Cancel));
    }

    [GeneratedRegex("letter-spacing:1px\">([A-Za-z0-9]+)</code>")]
    private static partial Regex FirstPassword();

    [Fact]
    public async Task A_merchant_with_a_parcel_on_the_road_is_not_terminated_and_only_the_super_admin_may_try()
    {
        WebAppFactory.RequireDatabase();
        var shop = await NewMerchantAsync();
        var code = await BookAsync(shop.ApiKey);
        var hub = await SignInAsync("onedrop", "hub@onedrop.test");
        await hub.SubmitAsync("/Hub/Scan?hub=MIR", "/Hub/Scan?mode=Receive&hub=MIR", ("Code", code));
        var admin = await SignInAsync("onedrop", "admin@onedrop.test");
        var page = $"/Admin/Merchant/{shop.Id}";

        Assert.Contains("still on the way", await admin.SubmitAsync(page, $"{page}?handler=Terminate"));

        // Accounts staff open the merchant's page but are refused the button, and the page does not offer it
        var accounts = await SignInAsync("onedrop", "accounts@onedrop.test");
        Assert.DoesNotContain("Terminate account", await accounts.PageAsync(page));
        var refused = await accounts.PostFormAsync(page, $"{page}?handler=Terminate");
        Assert.NotEqual(HttpStatusCode.OK, refused.StatusCode);
        Assert.False(await QueryAsync("onedrop", db => db.Merchants.Where(m => m.Id == shop.Id).Select(m => m.Archived).SingleAsync(Cancel)));
    }

    private async Task<string> SignInRefusedAsync(string email, string? password = null)
    {
        var response = await Visit("onedrop").PostFormAsync(
            "/Account/Login",
            "/Account/Login",
            ("Input.Email", email),
            ("Input.Password", password ?? WebAppFactory.Password));

        return await response.Content.ReadAsStringAsync(Cancel);
    }
}
