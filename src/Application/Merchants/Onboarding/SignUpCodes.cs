using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Application.Abstractions;
using Application.Notifications.SendEmails;
using Domain.Common;
using Domain.Merchants;

namespace Application.Merchants.Onboarding;

/// <summary>
/// The code a shop signing up is emailed, to prove the email is its own before its account is made. Sent at once,
/// not through the outbox, because the shop is waiting on the page for it and must be told there if it could not go.
/// </summary>
public class SignUpCodes(
    IAppDbContext db,
    ITenantContext tenantContext,
    IEmailSender email,
    IConnectionSettings connections,
    ITrackingLinks links,
    TimeProvider time)
{
    /// <summary>Whether a shop signing up is asked for a code: only when an email can reach it.</summary>
    public bool Required => connections.EmailsArrive;

    public async Task<Result> SendAsync(string address, string? name, CancellationToken cancellationToken = default)
    {
        var tenant = tenantContext.Tenant ?? throw new InvalidOperationException("A sign-up belongs to a courier.");
        var now = time.GetUtcNow().UtcDateTime;
        var key = address.Trim().ToLowerInvariant();
        var last = await db.SignUpCodes
            .Where(c => c.Email == key)
            .OrderByDescending(c => c.IssuedOn)
            .FirstOrDefaultAsync(cancellationToken);
        if (last is not null && !last.AllowsResend(now))
        {
            return Error.Conflict("signUpCode.wait", "A code was sent a moment ago. Wait a minute before asking for another.");
        }

        var code = RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6", System.Globalization.CultureInfo.InvariantCulture);
        db.SignUpCodes.Add(SignUpCode.Issue(key, Hash(key, code), now));
        await db.SaveChangesAsync(cancellationToken);

        var content = new EmailContent
        {
            Subject = $"{code} is your {tenant.Name} verification code",
            Preview = $"Enter {code} to finish creating your account.",
            Label = "Verify your email",
            Title = "Confirm your email address",
            Greeting = name is null ? null : $"Hello {name},",
            Paragraphs = [$"Enter this code on the sign-up page to confirm your email and create your {tenant.Name} account."],
            Highlight = ($"{code[..3]} {code[3..]}", $"works for {(int)SignUpCode.Lifetime.TotalMinutes} minutes"),
            Small = $"Did not try to sign up at {tenant.Name}? Ignore this email: no account is made without the code. Never share it with anyone, not even our staff."
        };
        try
        {
            var written = EmailLayout.Render(content, new EmailBrand(tenant.Name, tenant.SupportPhone, links.Page("")));
            await email.SendAsync(written with { To = address.Trim(), ToName = name }, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return Error.Conflict("signUpCode.send", "We could not send the code to that address. Check it, or try again in a minute.");
        }

        return Result.Success();
    }

    /// <summary>The code typed for <paramref name="address"/>: right, within its time and its tries, and once.</summary>
    public async Task<Result> CheckAsync(string address, string? code, CancellationToken cancellationToken = default)
    {
        var key = address.Trim().ToLowerInvariant();
        var typed = new string((code ?? "").Where(char.IsAsciiDigit).ToArray());
        var last = await db.SignUpCodes
            .Where(c => c.Email == key)
            .OrderByDescending(c => c.IssuedOn)
            .FirstOrDefaultAsync(cancellationToken);
        if (last is null)
        {
            return Error.Validation("signUpCode.none", "Ask for a code first.");
        }

        if (typed.Length != 6)
        {
            return Error.Validation("signUpCode.format", "Enter the 6-digit code from the email.");
        }

        var checkedCode = last.Check(Hash(key, typed), time.GetUtcNow().UtcDateTime);
        await db.SaveChangesAsync(cancellationToken);

        return checkedCode;
    }

    private static string Hash(string key, string code)
    {
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{key}:{code}")));
    }
}
