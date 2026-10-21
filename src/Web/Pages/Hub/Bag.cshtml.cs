using Microsoft.AspNetCore.Mvc;
using Application.Abstractions;
using Application.Hubs.Bags;
using Application.Hubs.HubOverview;

namespace Web.Pages.Hub;

/// <summary>
/// A bag's manifest: the paper that travels with it, with its QR code for the hub it is for to scan, every parcel in it
/// and lines to sign. A hub's own staff open only the bags from or to their hub; another courier's bag is not found.
/// </summary>
public class BagModel(HubOverviewHandler hubs, BagsHandler bags, ITenantContext tenantContext) : HubPage(hubs)
{
    public BagView? View { get; private set; }

    public string Courier => tenantContext.Tenant?.Name ?? "";

    public string Hotline => tenantContext.Tenant?.SupportPhone ?? "";

    public async Task<IActionResult> OnGetAsync(string number, CancellationToken cancellationToken)
    {
        if (!await LoadHubAsync(cancellationToken))
        {
            return NotFound();
        }

        View = await bags.BagAsync(number, cancellationToken);
        if (View is null || (LockedToHub && View.Bag.FromHubCode != Hub?.Code && View.Bag.ToHubCode != Hub?.Code))
        {
            return NotFound();
        }

        return Page();
    }
}
