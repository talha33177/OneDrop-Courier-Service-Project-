using Microsoft.AspNetCore.Mvc.RazorPages;
using Application.Parcels.Stuck;
using Domain.Parcels;

namespace Web.Pages.Admin;

/// <summary>Parcels that have stayed in one place longer than the courier allows, the longest first.</summary>
public class StuckModel(StuckParcelsHandler stuck) : PageModel
{
    public ParcelStatus? Status { get; private set; }

    public StuckParcels Stuck { get; private set; } = null!;

    public async Task OnGetAsync(ParcelStatus? status, CancellationToken cancellationToken)
    {
        Status = status is { } chosen && StuckParcelsHandler.Watched.Contains(chosen) ? chosen : null;
        Stuck = await stuck.ListAsync(Status, cancellationToken);
    }
}
