using Microsoft.AspNetCore.Mvc;
using Application.Hubs.HubOverview;
using Application.Hubs.HubScan;
using Domain.Staff;

namespace Web.Pages.Hub;

/// <summary>
/// Hub staff scan labels with a hand scanner (it types into the box and presses Enter) or type the code. Three modes:
/// receive a parcel, send it to another hub, or hand a return back to its merchant. The answer says what to do next.
/// Each mode is a desk's: receiving, line-haul or returns, and a person sees only the modes of the desks they work.
/// </summary>
public class ScanModel(HubOverviewHandler hubs, HubScanHandler scans) : HubPage(hubs)
{
    [BindProperty(SupportsGet = true)]
    public ScanMode Mode { get; set; }

    [BindProperty]
    public string? Code { get; set; }

    public ScanAnswer? Answer { get; private set; }

    /// <summary>The desk a scanning mode belongs to.</summary>
    public static StaffPermissions Needs(ScanMode mode)
    {
        return mode switch
        {
            ScanMode.Receive => StaffPermissions.Receive,
            ScanMode.Dispatch => StaffPermissions.Dispatch,
            _ => StaffPermissions.Returns
        };
    }

    public async Task<IActionResult> OnGetAsync(CancellationToken cancellationToken)
    {
        if (!Rights.May(Needs(Mode)))
        {
            // Opened on a mode of another desk (the scanner's default is Receive): the first of their own instead
            var mine = Enum.GetValues<ScanMode>().First(mode => Rights.May(Needs(mode)));

            return RedirectToPage(new { mode = mine, hub = Request.Query["hub"].ToString() is { Length: > 0 } hub ? hub : null });
        }

        return await LoadHubAsync(cancellationToken) ? Page() : NotFound();
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        if (!Rights.May(Needs(Mode)))
        {
            return Forbid();
        }

        if (!await LoadHubAsync(cancellationToken) || Hub is null)
        {
            return NotFound();
        }

        var scanned = await scans.ScanAsync(Hub.Code, Mode, Code, cancellationToken);
        if (scanned.IsFailure)
        {
            return NotFound();
        }

        Answer = scanned.Value;
        Code = null;
        ModelState.Clear();

        return Page();
    }
}
