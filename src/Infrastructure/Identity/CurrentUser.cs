using System.Globalization;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Application.Abstractions;
using Application.Common;

namespace Infrastructure.Identity;

/// <summary>Reads the caller from the request's principal. Outside a request (jobs, seeding) everything is null.</summary>
public class CurrentUser(IHttpContextAccessor accessor) : ICurrentUser
{
    public long? UserId => Read(ClaimTypes.NameIdentifier);

    /// <summary>
    /// The business cookie, when it names one of the login's own businesses (listed on its sign-in, so a forged cookie
    /// changes nothing); otherwise the account's main profile.
    /// </summary>
    public long? MerchantId
    {
        get
        {
            var account = Read(AppClaims.MerchantId);
            var user = accessor.HttpContext?.User;
            if (account is null || user is null ||
                accessor.HttpContext!.Request.Cookies[BusinessCookie.Name(user)] is not { } chosen)
            {
                return account;
            }

            var mine = user.FindFirstValue(AppClaims.Businesses)?.Split(' ') ?? [];

            return mine.Contains(chosen) ? long.Parse(chosen, CultureInfo.InvariantCulture) : account;
        }
    }

    public long? AccountId => UserId is null ? null : Read(AppClaims.MerchantId);

    public bool IsInRole(string role)
    {
        return accessor.HttpContext?.User.IsInRole(role) == true;
    }

    private long? Read(string claimType)
    {
        var value = accessor.HttpContext?.User.FindFirstValue(claimType);

        return long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var id) ? id : null;
    }
}

/// <summary>The cookie that remembers which business a merchant login is working in, one per login.</summary>
public static class BusinessCookie
{
    public static string Name(ClaimsPrincipal user)
    {
        return $"business-{user.FindFirstValue(ClaimTypes.NameIdentifier)}";
    }
}
