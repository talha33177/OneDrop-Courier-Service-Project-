using Domain.Common;

namespace Domain.Merchants;

/// <summary>
/// A code emailed to a shop signing up, proving it reads the email it gave before its account is made. Only the code's
/// hash is kept. It works for <see cref="Lifetime"/>, takes <see cref="MaxAttempts"/> wrong guesses, and works once;
/// a new one may be asked for after <see cref="ResendAfter"/>.
/// </summary>
public class SignUpCode : TenantEntity
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);

    public static readonly TimeSpan ResendAfter = TimeSpan.FromMinutes(1);

    public const int MaxAttempts = 5;

    private SignUpCode()
    {
    }

    /// <summary>The address, in lower case.</summary>
    public string Email { get; private set; } = "";

    public string CodeHash { get; private set; } = "";

    public DateTime IssuedOn { get; private set; }

    public DateTime ExpiresOn { get; private set; }

    public int Attempts { get; private set; }

    public DateTime? VerifiedOn { get; private set; }

    public static SignUpCode Issue(string email, string codeHash, DateTime now)
    {
        return new SignUpCode
        {
            Email = email.Trim().ToLowerInvariant(),
            CodeHash = codeHash,
            IssuedOn = now,
            ExpiresOn = now + Lifetime
        };
    }

    /// <summary>Whether a new code may be sent yet, after this one.</summary>
    public bool AllowsResend(DateTime now)
    {
        return now >= IssuedOn + ResendAfter;
    }

    /// <summary>The code typed, by its hash: right once, within its time and its tries.</summary>
    public Result Check(string codeHash, DateTime now)
    {
        if (VerifiedOn is not null)
        {
            return Error.Conflict("signUpCode.used", "This code was used already. Ask for a new one.");
        }

        if (now > ExpiresOn)
        {
            return Error.Validation("signUpCode.expired", "This code has expired. Ask for a new one.");
        }

        if (Attempts >= MaxAttempts)
        {
            return Error.Validation("signUpCode.attempts", "Too many wrong codes. Ask for a new one.");
        }

        if (!string.Equals(codeHash, CodeHash, StringComparison.Ordinal))
        {
            Attempts++;
            var left = MaxAttempts - Attempts;

            return Error.Validation(
                "signUpCode.wrong",
                left > 0 ? $"That code is not right. {left} {(left == 1 ? "try" : "tries")} left." : "That code is not right. Ask for a new one.");
        }

        VerifiedOn = now;

        return Result.Success();
    }
}
