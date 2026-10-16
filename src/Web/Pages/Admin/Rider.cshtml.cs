using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Application.Delivery.Riders;
using Domain.Common;

namespace Web.Pages.Admin;

/// <summary>
/// One rider for the admin: what they carry now, and over a chosen period their runs, doors and cash collected against
/// cash handed in.
/// </summary>
public class RiderModel(AdminRidersHandler riders) : PageModel
{
    [BindProperty(SupportsGet = true, Name = "period")]
    public PeriodKind Kind { get; set; } = PeriodKind.Last30Days;

    [BindProperty(SupportsGet = true)]
    public DateOnly? From { get; set; }

    [BindProperty(SupportsGet = true)]
    public DateOnly? To { get; set; }

    public RiderDetails Details { get; private set; } = null!;

    /// <summary>Why the days asked for cannot be shown.</summary>
    public string? Problem { get; private set; }

    public async Task<IActionResult> OnGetAsync(long id, CancellationToken cancellationToken)
    {
        var found = await riders.DetailsAsync(id, Kind, From, To, cancellationToken);
        if (found.Error == AdminRidersHandler.NotFound)
        {
            return NotFound();
        }

        if (found.IsFailure)
        {
            // Days that cannot be read: say why, and show the last 30 days meanwhile
            Problem = found.Error!.Message;
            Kind = PeriodKind.Last30Days;
            found = await riders.DetailsAsync(id, Kind, null, null, cancellationToken);
        }

        Details = found.Value;

        return Page();
    }
}
