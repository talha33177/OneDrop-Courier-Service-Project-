using Microsoft.AspNetCore.Mvc;
using Application.Hubs.HubOverview;
using Application.Hubs.HubBoard;
using Application.Hubs.HubScan;
using Web.Authentication;

namespace Web.Pages.Hub;

/// <summary>
/// The hub's day at a glance: what comes in, what waits for a rider, what to send on and what to hand back. Or the
/// same over every hub, with each list saying which hub its work is at; a step then opens the chooser for its page.
/// </summary>
public class IndexModel(HubOverviewHandler hubs, HubBoardHandler board) : HubPage(hubs)
{
    public HubBoard? Board { get; private set; }

    public override bool OffersAllHubs => true;

    /// <summary>Whether a step's page, and its scanner mode, is one of this person's desks.</summary>
    public bool MayGo(string page, object? mode)
    {
        var scan = mode as ScanMode? ?? (Enum.TryParse<ScanMode>(mode?.ToString(), out var named) ? named : null);

        return StaffPages.MayOpen(Rights, page) && (scan is null || Rights.May(ScanModel.Needs(scan.Value)));
    }

    public async Task<IActionResult> OnGetAsync(CancellationToken cancellationToken)
    {
        if (!await LoadHubAsync(cancellationToken))
        {
            return NotFound();
        }

        if (AllHubs && Work.Count > 0)
        {
            Board = await board.GetAllAsync(cancellationToken);
        }
        else if (Hub is not null)
        {
            Board = await board.GetAsync(Hub.Code, cancellationToken);
        }

        return Page();
    }
}
