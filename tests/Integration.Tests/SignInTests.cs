using System.Net;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Infrastructure.Seeding;

namespace Integration.Tests;

/// <summary>Signing in: wrong passwords are told so and never lock a login, however many are tried.</summary>
public class SignInTests(WebAppFactory factory) : AppTests(factory)
{
    [Fact]
    public async Task Wrong_passwords_lock_nothing_however_many_are_tried()
    {
        WebAppFactory.RequireDatabase();
        var email = $"{Guid.NewGuid():N}@signin.example";
        await Factory.Services
            .GetRequiredService<AdminSetup>()
            .EnsureCourierAdminAsync("onedrop", email, "Sign-in admin", "Chosen#2026", Cancel);

        for (var attempt = 0; attempt < 8; attempt++)
        {
            var wrong = await Visit("onedrop").PostFormAsync(
                "/Account/Login",
                "/Account/Login",
                ("Input.Email", email),
                ("Input.Password", $"Wrong#{attempt}2026"));
            Assert.Equal(HttpStatusCode.OK, wrong.StatusCode);
            var page = await wrong.Content.ReadAsStringAsync(Cancel);
            Assert.Contains("is not right for", page);
            Assert.DoesNotContain("Too many attempts", page);
        }

        // The right password still signs in at once, and none of the tries was held against the login
        var admin = Visit("onedrop", await SignInCookieAsync("onedrop", email, "Chosen#2026"));
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync("/Admin")).StatusCode);

        await using var connection = new SqlConnection(WebAppFactory.ConnectionString);
        await connection.OpenAsync(Cancel);
        await using var command = new SqlCommand(
            $"SELECT AccessFailedCount, LockoutEnd FROM [Identity].[User] WHERE NormalizedUserName = N'{email.ToUpperInvariant()}'",
            connection);
        await using var reader = await command.ExecuteReaderAsync(Cancel);
        Assert.True(await reader.ReadAsync(Cancel));
        Assert.Equal(0, reader.GetInt32(0));
        Assert.True(reader.IsDBNull(1));
    }

    /// <summary>
    /// A login the old rule had locked before the rule went stays locked in the database, and sign-in refuses a lock
    /// whatever the setting, so the script lets everyone back in who was not stopped by an admin or an owner.
    /// </summary>
    [Fact]
    public async Task The_script_lets_back_in_logins_the_old_rule_locked_and_leaves_stopped_ones_out()
    {
        WebAppFactory.RequireDatabase();
        var setup = Factory.Services.GetRequiredService<AdminSetup>();
        var locked = $"{Guid.NewGuid():N}@signin.example";
        var stopped = $"{Guid.NewGuid():N}@signin.example";
        await setup.EnsureCourierAdminAsync("onedrop", locked, "Locked out", "Chosen#2026", Cancel);
        await setup.EnsureCourierAdminAsync("onedrop", stopped, "Stopped", "Chosen#2026", Cancel);

        // One locked by the old rule's five wrong tries, one stopped by an admin (archived, locked to the end of time)
        await Sql(
            "UPDATE [Identity].[User] SET LockoutEnd = DATEADD(MINUTE, 5, SYSDATETIMEOFFSET()), AccessFailedCount = 5 " +
            $"WHERE NormalizedUserName = N'{locked.ToUpperInvariant()}'");
        await Sql(
            "UPDATE [Identity].[User] SET Archived = 1, LockoutEnabled = 1, LockoutEnd = '9999-12-31 23:59:59 +00:00' " +
            $"WHERE NormalizedUserName = N'{stopped.ToUpperInvariant()}'");
        Assert.Contains("has been stopped", await SignInPageAsync(locked, "Chosen#2026"));

        await Sql(await ScriptAsync("013_ClearOldLockouts.sql"));

        var admin = Visit("onedrop", await SignInCookieAsync("onedrop", locked, "Chosen#2026"));
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync("/Admin")).StatusCode);
        Assert.Contains("has been stopped", await SignInPageAsync(stopped, "Chosen#2026"));
    }

    private async Task<string> SignInPageAsync(string email, string password)
    {
        var response = await Visit("onedrop").PostFormAsync(
            "/Account/Login",
            "/Account/Login",
            ("Input.Email", email),
            ("Input.Password", password));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return await response.Content.ReadAsStringAsync(Cancel);
    }
}
