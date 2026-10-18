using System.Net;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Application.Notifications.SendEmails;
using Infrastructure.Email;

namespace Integration.Tests;

/// <summary>
/// The emails people get, read from the fake mail server's log after the email job has run: a sign-up welcomes the
/// shop and tells the courier's super admins, an approval and a credit tell the shop, and "Forgot your password?"
/// sends a link that sets a new password once.
/// </summary>
public partial class EmailsTests(WebAppFactory factory) : AppTests(factory)
{
    private EmailLog Log => Factory.Services.GetRequiredService<EmailLog>();

    [Fact]
    public async Task A_sign_up_welcomes_the_shop_tells_the_super_admins_and_its_approval_and_a_credit_reach_the_shop()
    {
        WebAppFactory.RequireDatabase();
        var shop = await NewMerchantAsync(approved: false);
        await SendEmailsAsync("onedrop");

        var welcome = Assert.Single(Log.Recent, e => e.To == shop.Email && e.Subject.StartsWith("Welcome to", StringComparison.Ordinal));
        Assert.StartsWith("<!doctype html>", welcome.Html);
        Assert.Contains(WebUtility.HtmlEncode(shop.Name), welcome.Html);
        Assert.Contains("Merchant", welcome.Text);
        Assert.Contains(Log.Recent, e => e.To == "admin@onedrop.test" && e.Subject == $"New sign-up waiting for approval: {shop.Name}");

        var admin = await SignInAsync("onedrop", "admin@onedrop.test");
        await admin.PostFormAsync($"/Admin/Merchant/{shop.Id}", $"/Admin/Merchant/{shop.Id}?handler=Approve");
        await admin.PostFormAsync(
            $"/Admin/Merchant/{shop.Id}",
            $"/Admin/Merchant/{shop.Id}?handler=Adjust",
            ("direction", "credit"),
            ("amount", "200"),
            ("note", "Compensation for a damaged parcel"));
        await SendEmailsAsync("onedrop");

        Assert.Contains(Log.Recent, e => e.To == shop.Email && e.Subject.StartsWith("You're approved", StringComparison.Ordinal));
        var credit = Assert.Single(Log.Recent, e => e.To == shop.Email && e.Subject == "We added ৳200 to your balance");
        Assert.Contains("Compensation for a damaged parcel", credit.Html);

        // Another courier's admins hear nothing of it
        Assert.DoesNotContain(Log.Recent, e => e.To == "admin@rival.test" && e.Subject.Contains(shop.Name, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Forgot_password_sends_a_link_that_sets_a_new_password_once_and_says_the_same_for_an_unknown_email()
    {
        WebAppFactory.RequireDatabase();
        var shop = await NewMerchantAsync();
        var visitor = Visit("onedrop");

        Assert.Contains("Check your email", await visitor.SubmitAsync("/Account/ForgotPassword", "/Account/ForgotPassword", ("Email", shop.Email)));
        var nobody = $"{Guid.NewGuid():N}@nobody.test";
        Assert.Contains("Check your email", await visitor.SubmitAsync("/Account/ForgotPassword", "/Account/ForgotPassword", ("Email", nobody)));
        await SendEmailsAsync("onedrop");
        Assert.DoesNotContain(Log.Recent, e => e.To == nobody);

        var email = Assert.Single(Log.Recent, e => e.To == shop.Email && e.Subject.StartsWith("Choose a new password", StringComparison.Ordinal));
        var query = ResetLink().Match(email.Text).Groups[1].Value;
        Assert.NotEmpty(query);
        var page = $"/Account/ResetPassword?{query}";
        var fields = query.Split('&').Select(part => part.Split('=', 2)).ToDictionary(part => part[0], part => Uri.UnescapeDataString(part[1]));
        const string chosen = "Chosen#2026x";

        var mismatched = await visitor.SubmitAsync(page, page, ("user", fields["user"]), ("Token", fields["token"]), ("password", chosen), ("confirm", "Other#2026x"));
        Assert.Contains("The two passwords are not the same", mismatched);

        var reset = await visitor.PostFormAsync(page, page, ("user", fields["user"]), ("Token", fields["token"]), ("password", chosen), ("confirm", chosen));
        Assert.Equal(HttpStatusCode.Redirect, reset.StatusCode);
        Assert.NotEmpty(await SignInCookieAsync("onedrop", shop.Email, chosen));

        // The same link does not work twice
        var again = await visitor.SubmitAsync(page, page, ("user", fields["user"]), ("Token", fields["token"]), ("password", "Another#2026x"), ("confirm", "Another#2026x"));
        Assert.Contains("expired or was used already", again);
    }

    private async Task SendEmailsAsync(string slug)
    {
        await using var scope = await ScopeAsync(slug);
        await scope.ServiceProvider.GetRequiredService<SendEmailsJob>().RunAsync(Cancel);
    }

    [GeneratedRegex(@"/Account/ResetPassword\?(\S+)")]
    private static partial Regex ResetLink();
}
