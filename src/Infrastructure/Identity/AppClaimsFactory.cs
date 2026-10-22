using System.Globalization;
using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Application.Abstractions;
using Application.Common;

namespace Infrastructure.Identity;

/// <summary>
/// Puts the tenant and merchant ids on the cookie so every request can scope itself without a lookup, and a merchant
/// login's other businesses, the only ones it may switch to.
/// </summary>
public class AppClaimsFactory(
    UserManager<AppUser> userManager,
    RoleManager<AppRole> roleManager,
    IOptions<IdentityOptions> options,
    IAppDbContext db)
    : UserClaimsPrincipalFactory<AppUser, AppRole>(userManager, roleManager, options)
{
    protected override async Task<ClaimsIdentity> GenerateClaimsAsync(AppUser user)
    {
        var identity = await base.GenerateClaimsAsync(user);
        identity.AddClaim(new Claim(ClaimTypes.GivenName, user.DisplayName));
        AddIfSet(identity, AppClaims.TenantId, user.TenantId);
        AddIfSet(identity, AppClaims.MerchantId, user.MerchantId);
        if (user.MerchantId is { } account)
        {
            // The merchant filter would hide them while the login works in one business; the account is the point here
            var businesses = await db.Merchants
                .IgnoreQueryFilters([QueryFilters.Merchant])
                .Where(m => m.MainMerchantId == account)
                .Select(m => m.Id)
                .ToListAsync();
            if (businesses.Count > 0)
            {
                identity.AddClaim(new Claim(
                    AppClaims.Businesses,
                    string.Join(' ', businesses.Select(id => id.ToString(CultureInfo.InvariantCulture)))));
            }
        }

        return identity;
    }

    private static void AddIfSet(ClaimsIdentity identity, string type, long? value)
    {
        if (value.HasValue)
        {
            identity.AddClaim(new Claim(type, value.Value.ToString(CultureInfo.InvariantCulture)));
        }
    }
}
