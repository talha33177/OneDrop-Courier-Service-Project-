using System.Net;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;

namespace Integration.Tests;

/// <summary>
/// The courier's own people on /Admin/Staff: an admin adds relief staff with a first password shown once, makes them an
/// admin, stops them and lets them back in; nobody stops themselves, and another courier's admin reaches none of it.
/// </summary>
public partial class StaffTests(WebAppFactory factory) : AppTests(factory)
{
    [Fact]
    public async Task A_super_admin_adds_relief_staff_makes_them_super_admin_and_stops_them()
    {
        WebAppFactory.RequireDatabase();
        var admin = await SignInAsync("onedrop", "admin@onedrop.test");
        var email = $"{Guid.NewGuid():N}@staff.example";

        // Added as hub staff, with a password shown once that signs them in to the hubs and not to the admin pages
        var added = await admin.SubmitAsync("/Admin/Staff", "/Admin/Staff?handler=Add", ("Name", "Karim Mia"), ("Email", email), ("Job", "Relief"));
        Assert.Contains("Their first password", added);
        var password = FirstPassword().Match(added).Groups[1].Value;
        var cookie = await SignInCookieAsync("onedrop", email, password);
        var staff = Visit("onedrop", cookie);
        Assert.Equal(HttpStatusCode.OK, (await staff.GetAsync("/Hub/Choose")).StatusCode);
        Assert.NotEqual(HttpStatusCode.OK, (await staff.GetAsync("/Admin/Staff")).StatusCode);
        Assert.Contains("already has an account", await admin.SubmitAsync("/Admin/Staff", "/Admin/Staff?handler=Add", ("Name", "Again"), ("Email", email), ("Job", "Relief")));
        Assert.Contains("Choose their job", await admin.SubmitAsync("/Admin/Staff", "/Admin/Staff?handler=Add", ("Name", "Rider?"), ("Email", $"{Guid.NewGuid():N}@staff.example"), ("Job", "Rider")));

        // Made a super admin: from their next sign-in the admin pages open
        var id = await IdAsync(email);
        Assert.Contains("Saved", await admin.SubmitAsync("/Admin/Staff", $"/Admin/Staff?handler=Change&id={id}", ("Name", "Karim Mia"), ("Job", "SuperAdmin")));
        var promoted = Visit("onedrop", await SignInCookieAsync("onedrop", email, password));
        Assert.Equal(HttpStatusCode.OK, (await promoted.GetAsync("/Admin/Staff")).StatusCode);

        // Nobody stops themselves; the new admin may stop another, and the stopped login cannot sign in
        var me = await IdAsync("admin@onedrop.test");
        Assert.Contains("cannot stop yourself", await promoted.SubmitAsync("/Admin/Staff", $"/Admin/Staff?handler=Stop&id={id}"));
        Assert.Contains("cannot stop yourself", await admin.SubmitAsync("/Admin/Staff", $"/Admin/Staff?handler=Stop&id={me}"));
        Assert.Contains("stopped and cannot sign in", await admin.SubmitAsync("/Admin/Staff", $"/Admin/Staff?handler=Stop&id={id}"));
        var refused = await Visit("onedrop").PostFormAsync("/Account/Login", "/Account/Login", ("Input.Email", email), ("Input.Password", password));
        Assert.NotEqual(HttpStatusCode.Redirect, refused.StatusCode);

        // Let back in with the same password; a new password replaces it
        Assert.Contains("sign in again", await admin.SubmitAsync("/Admin/Staff", $"/Admin/Staff?handler=LetBackIn&id={id}"));
        var reset = await admin.SubmitAsync("/Admin/Staff", $"/Admin/Staff?handler=Reset&id={id}");
        var newPassword = FirstPassword().Match(reset).Groups[1].Value;
        Assert.NotEqual(password, newPassword);
        await SignInCookieAsync("onedrop", email, newPassword);

        // A rider's or merchant's login is not staff, and another courier's admin finds none of this courier's people
        var rider = await NewRiderAsync("MIR");
        var riderId = await QueryAsync("onedrop", db => db.Riders.Where(r => r.Id == rider.Id).Select(r => r.UserId).SingleAsync(Cancel));
        Assert.Equal(HttpStatusCode.NotFound, (await admin.PostFormAsync("/Admin/Staff", $"/Admin/Staff?handler=Stop&id={riderId}")).StatusCode);
        var rival = await SignInAsync("rival", "admin@rival.test");
        Assert.DoesNotContain(email, await rival.PageAsync("/Admin/Staff"));
        Assert.Equal(HttpStatusCode.NotFound, (await rival.PostFormAsync("/Admin/Staff", $"/Admin/Staff?handler=Stop&id={id}")).StatusCode);
    }

    /// <summary>A person's login id at OneDrop Courier.</summary>
    private Task<long> IdAsync(string email)
    {
        return QueryAsync("onedrop", db => db.Users.Where(u => u.Email == email).Select(u => u.Id).SingleAsync(Cancel));
    }

    [GeneratedRegex("letter-spacing:1px\">([A-Za-z0-9]+)</code>")]
    private static partial Regex FirstPassword();
}
