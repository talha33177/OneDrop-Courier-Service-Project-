using Microsoft.AspNetCore.Mvc.RazorPages;
using Application.Abstractions;
using Application.Common;
using Application.Delivery.Pickups;

namespace Web.Pages.Merchant;

/// <summary>
/// The courier's daily pickup as the merchant sees it: when it comes, which parcels go with today's and which with
/// tomorrow's, and the pickups so far. Nothing to ask for: every parcel booked is on the day's list by itself.
/// </summary>
public class PickupsModel(PickupsHandler pickups, ITenantContext tenantContext, TimeProvider time) : PageModel
{
    public MerchantPickups Data { get; private set; } = null!;

    public DateOnly Today { get; private set; }

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        Data = await pickups.ForMerchantAsync(cancellationToken);
        Today = tenantContext.Require().Today(time.GetUtcNow().UtcDateTime);
    }
}
