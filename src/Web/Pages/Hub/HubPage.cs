using Microsoft.AspNetCore.Mvc.RazorPages;
using Application.Hubs;
using Application.Hubs.HubOverview;
using Application.Platform.Staff;
using Web.Authentication;

namespace Web.Pages.Hub;

/// <summary>
/// A page about one hub: the hub named in the request or the one remembered (<see cref="RememberHub"/>); before either,
/// staff are sent to choose one. Every hub page still finds the hub through the courier's own list, so another
/// courier's code is not found. The hub menu shows what waits at each hub.
/// </summary>
public abstract class HubPage(HubOverviewHandler overview) : PageModel
{
    public IReadOnlyList<HubWork> Work { get; private set; } = [];

    public IReadOnlyList<HubItem> Hubs => [.. Work.Select(work => work.Hub)];

    public HubItem? Hub { get; private set; }

    /// <summary>What the person signed in may do, so the page offers only that.</summary>
    public StaffRights Rights => StaffPages.Rights(HttpContext) ?? StaffRights.Nobody;

    /// <summary>A hub's own staff: no hub menu, their hub is the only one.</summary>
    public bool LockedToHub => !Rights.AtHeadOffice;

    /// <summary>Whether the page can show every hub at once; one that works at a hub (scan, assign) cannot.</summary>
    public virtual bool OffersAllHubs => false;

    /// <summary>The page shows every hub: asked for in the hub menu, remembered, or an admin who has not chosen.</summary>
    public bool AllHubs { get; private set; }

    /// <summary>Loads the hubs and picks the current one; false when the code names no hub of this courier.</summary>
    protected async Task<bool> LoadHubAsync(CancellationToken cancellationToken)
    {
        Work = await overview.ListAsync(cancellationToken);
        var code = RememberHub.Of(HttpContext);
        AllHubs = code is null && OffersAllHubs && RememberHub.WantsAll(HttpContext);
        if (AllHubs)
        {
            return true;
        }

        Hub = code is null ? Hubs.FirstOrDefault() : Hubs.FirstOrDefault(h => h.Code == code);

        return Hub is not null || (code is null && Work.Count == 0);
    }
}
