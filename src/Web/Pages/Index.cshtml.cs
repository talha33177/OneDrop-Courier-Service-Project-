using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Application.Abstractions;
using Application.Common;
using Application.Platform.Staff;
using Application.Pricing.DeliveryOptions;
using Application.Pricing.Rates;
using Web.Authentication;
using Web.MultiTenancy;

namespace Web.Pages;

/// <summary>
/// The front page. A signed-in user goes to their own panel; a visitor to a courier's site sees what it offers, its
/// prices and a box to track a parcel. A visitor to the bare platform domain is sent to the home courier's site
/// (<see cref="TenancyOptions.HomeCourier"/>), or, with none, sees the list of couriers.
/// </summary>
public class IndexModel(
    ITenantContext tenantContext,
    ITenantCatalog catalog,
    IAppDbContext db,
    RatesHandler rates,
    DeliveryOptionsHandler deliveryOptions,
    StaffAccess staff,
    IOptions<TenancyOptions> tenancy) : PageModel
{
    public TenantInfo? Tenant => tenantContext.Tenant;

    public IReadOnlyList<(TenantInfo Tenant, string Url)> Couriers { get; private set; } = [];

    public IReadOnlyList<RateRow> Rates { get; private set; } = [];

    /// <summary>The delivery speeds and kinds of contents on sale, for the price cards.</summary>
    public IReadOnlyList<OptionRow> DeliveryTypes { get; private set; } = [];

    public int Cities { get; private set; }

    public int Hubs { get; private set; }

    public int Areas { get; private set; }

    public async Task<IActionResult> OnGetAsync(CancellationToken cancellationToken)
    {
        var home = User.IsInRole(Roles.Merchant) ? "/Merchant/Index"
            : User.IsInRole(Roles.Rider) ? "/Rider/Index"
            : User.IsInRole(Roles.TenantAdmin) ? "/Admin/Index"
            : User.IsInRole(Roles.HubStaff) ? await StaffHomeAsync(cancellationToken)
            : User.IsInRole(Roles.PlatformAdmin) ? "/Platform/Tenants"
            : null;
        if (home is not null)
        {
            return RedirectToPage(home);
        }

        if (Tenant is null)
        {
            if (tenancy.Value.HomeCourier is { Length: > 0 } slug && await catalog.FindBySlugAsync(slug, cancellationToken) is not null)
            {
                return Redirect(tenancy.Value.TenantUrl(Request, slug));
            }

            var tenants = await catalog.ListAsync(cancellationToken);
            Couriers = [.. tenants.Select(t => (t, tenancy.Value.TenantUrl(Request, t.Slug)))];

            return Page();
        }

        Rates = await rates.ListAsync(cancellationToken);
        DeliveryTypes = await deliveryOptions.ListAsync(cancellationToken: cancellationToken);
        Cities = await db.Zones.Where(z => !z.Archived).Select(z => z.City).Distinct().CountAsync(cancellationToken);
        Hubs = await db.Hubs.CountAsync(h => !h.Archived, cancellationToken);
        Areas = await db.Areas.CountAsync(a => !a.Archived, cancellationToken);

        return Page();
    }

    /// <summary>
    /// Where a staff member starts: operations on the dashboard, a hub desk on the board, accounts on the payouts,
    /// merchant support on the merchants, customer support on the parcel search.
    /// </summary>
    private async Task<string> StaffHomeAsync(CancellationToken cancellationToken)
    {
        var rights = await staff.CurrentAsync(cancellationToken);
        string[] homes = ["/Admin/Index", "/Hub/Index", "/Admin/Payouts", "/Admin/Merchants", "/Admin/Coverage", "/Hub/Parcels"];

        return homes.FirstOrDefault(page => StaffPages.MayOpen(rights, page)) ?? "/Account/AccessDenied";
    }
}
