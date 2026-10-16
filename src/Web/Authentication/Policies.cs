using Microsoft.AspNetCore.Authorization;
using Application.Common;

namespace Web.Authentication;

public static class Policies
{
    public const string MerchantApi = nameof(MerchantApi);
    public const string MerchantPortal = nameof(MerchantPortal);
    public const string PlatformAdmin = nameof(PlatformAdmin);

    /// <summary>
    /// The courier's own staff: super admins and staff with a job. Which hub and admin pages each may open is then
    /// decided by <see cref="StaffPermissionFilter"/>.
    /// </summary>
    public const string Operations = nameof(Operations);

    /// <summary>The courier's riders, on their own tenant's subdomain.</summary>
    public const string Rider = nameof(Rider);

    public static void Configure(AuthorizationOptions options)
    {
        options.AddPolicy(MerchantApi, policy => policy
            .AddAuthenticationSchemes(ApiKeyAuthenticationHandler.SchemeName)
            .RequireRole(Roles.Merchant)
            .RequireClaim(AppClaims.MerchantId));
        options.AddPolicy(MerchantPortal, policy => policy
            .RequireRole(Roles.Merchant)
            .RequireClaim(AppClaims.MerchantId));
        options.AddPolicy(PlatformAdmin, policy => policy.RequireRole(Roles.PlatformAdmin));
        options.AddPolicy(Operations, policy => policy
            .RequireRole(Roles.HubStaff, Roles.TenantAdmin)
            .RequireClaim(AppClaims.TenantId));
        options.AddPolicy(Rider, policy => policy
            .RequireRole(Roles.Rider)
            .RequireClaim(AppClaims.TenantId));
    }
}
