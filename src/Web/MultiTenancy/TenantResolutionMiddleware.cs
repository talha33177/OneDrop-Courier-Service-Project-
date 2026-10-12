using System.Globalization;
using Microsoft.Extensions.Options;
using Application.Abstractions;
using Application.Common;
using Infrastructure.MultiTenancy;
using Serilog.Context;

namespace Web.MultiTenancy;

/// <summary>
/// First step of every request: onedrop.{root domain}, or the one address in Tenancy:HomeCourierHost, selects the
/// OneDrop courier. An unknown subdomain is a 404, never a fall-back to some default tenant. API calls may use the bare
/// domain; the API key selects the tenant for them (see ApiKeyAuthenticationHandler).
/// </summary>
public class TenantResolutionMiddleware(RequestDelegate next, IOptions<TenancyOptions> options)
{
    public async Task InvokeAsync(HttpContext context, ITenantCatalog catalog, TenantContext tenantContext)
    {
        var slug = options.Value.SlugFromHost(context.Request.Host.Host);
        if (slug is not null)
        {
            var tenant = await catalog.FindBySlugAsync(slug, context.RequestAborted);
            if (tenant is null)
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                await context.Response.WriteAsync($"There is no courier called '{slug}'.");
                return;
            }

            tenantContext.Set(tenant);
        }

        using (LogContext.PushProperty("Tenant", slug ?? "platform"))
        {
            await next(context);
        }
    }
}

/// <summary>
/// Runs after authentication. A login belongs to exactly one tenant (or to the platform); a cookie presented on
/// another tenant's host is refused. Cookies are host-only, so this should never fire - it is here in case
/// one day they are not.
/// </summary>
public class TenantUserGuardMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, ITenantContext tenantContext)
    {
        if (context.User.Identity?.IsAuthenticated == true)
        {
            var claimed = context.User.FindFirst(AppClaims.TenantId)?.Value;
            var current = tenantContext.TenantId?.ToString(CultureInfo.InvariantCulture);
            if (claimed != current)
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                await context.Response.WriteAsync("This login belongs to another courier.");
                return;
            }
        }

        await next(context);
    }
}
