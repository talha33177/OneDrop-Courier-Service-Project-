using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Application.Hubs.HubOverview;
using Application.Hubs.HubScan;

namespace Web.Pages.Hub;

/// <summary>
/// Where to work: every hub with what waits there, busiest first. A hub page opened before a hub is chosen comes here,
/// and so do the admin dashboard's tiles; when only one hub has the work asked for, it opens that hub straight away.
/// </summary>
public class ChooseModel(HubOverviewHandler overview) : PageModel
{
    private static readonly string[] Targets = ["Index", "Pickups", "Assign", "Scan", "Runs", "Returns", "Bags", "Cash"];

    public IReadOnlyList<HubWork> Busy { get; private set; } = [];

    public IReadOnlyList<HubWork> Quiet { get; private set; } = [];

    public string Next { get; private set; } = "Index";

    /// <summary>The scan page's mode, carried to the hub chosen (the all-hubs board's "Coming in" and "Send" steps).</summary>
    public ScanMode? Mode { get; private set; }

    public async Task<IActionResult> OnGetAsync(string? next, ScanMode? mode, CancellationToken cancellationToken)
    {
        Next = Targets.FirstOrDefault(t => t.Equals(next, StringComparison.OrdinalIgnoreCase)) ?? "Index";
        Mode = Next == "Scan" ? mode : null;
        var hubs = await overview.ListAsync(cancellationToken);
        var withWork = hubs
            .Where(work => Next switch
            {
                "Pickups" => work.PickupsOpen > 0,
                "Assign" => work.ToAssign > 0,
                "Runs" => work.OpenRuns > 0,
                "Scan" => Mode == ScanMode.Receive && work.Incoming > 0,
                "Cash" => work.CashWaiting,
                _ => false
            })
            .ToList();
        if (hubs.Count == 1 || withWork.Count == 1)
        {
            return RedirectToPage($"/Hub/{Next}", new { hub = (hubs.Count == 1 ? hubs[0] : withWork[0]).Hub.Code, mode = Mode });
        }

        // The cash page is about money, so the hubs holding cash come first, the most cash first
        if (Next == "Cash")
        {
            Busy = [.. hubs.Where(work => work.CashWaiting).OrderByDescending(work => work.CashAtHub).ThenBy(work => work.Hub.Name)];
            Quiet = [.. hubs.Where(work => !work.CashWaiting)];

            return Page();
        }

        Busy = [.. hubs.Where(work => work.Total > 0).OrderByDescending(work => work.Total).ThenBy(work => work.Hub.Name)];
        Quiet = [.. hubs.Where(work => work.Total == 0)];

        return Page();
    }
}
