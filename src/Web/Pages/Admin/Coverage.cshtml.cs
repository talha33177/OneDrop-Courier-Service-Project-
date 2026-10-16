using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Application.Network.Coverage;
using Domain.Common;

namespace Web.Pages.Admin;

/// <summary>Which part of the coverage page is open: the map it reads as, or the list that is kept.</summary>
public enum CoverageTab
{
    Map,
    Hubs,
    Zones,
    Areas
}

/// <summary>
/// The coverage map and the admin's way of keeping it: open and close hubs, draw zones on them with the city and
/// suburb flag that price a parcel, and keep the areas addresses are picked from. Nothing is deleted; what leaves the
/// map is archived, so the parcels that used it keep their route and their charge.
/// </summary>
public class CoverageModel(CoverageHandler coverage, CoverageAdminHandler admin) : PageModel
{
    [BindProperty(SupportsGet = true)]
    public CoverageTab Tab { get; set; }

    [BindProperty(SupportsGet = true)]
    public long? Edit { get; set; }

    /// <summary>The zone the areas tab is filtered to; 87 areas are too many to read at once.</summary>
    [BindProperty(SupportsGet = true)]
    public long? ZoneId { get; set; }

    public IReadOnlyList<CoverageHub> Map { get; private set; } = [];

    public IReadOnlyList<HubRow> Hubs { get; private set; } = [];

    public IReadOnlyList<ZoneRow> Zones { get; private set; } = [];

    public IReadOnlyList<AreaRow> Areas { get; private set; } = [];

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        await LoadAsync(cancellationToken);
    }

    public async Task<IActionResult> OnPostAddHubAsync(
        string? code,
        string? name,
        string? address,
        string? phone,
        CancellationToken cancellationToken)
    {
        var added = await admin.AddHubAsync(new NewHub(code, name, address, phone), cancellationToken);

        return Answer(added, $"{name} is open. Give it a zone to serve next.", ("/Admin/Coverage?tab=Zones", "Draw a zone on it"));
    }

    public async Task<IActionResult> OnPostEditHubAsync(
        long id,
        string? code,
        string? name,
        string? address,
        string? phone,
        CancellationToken cancellationToken)
    {
        return Answer(await admin.EditHubAsync(id, new NewHub(code, name, address, phone), cancellationToken), "Hub saved.");
    }

    public async Task<IActionResult> OnPostHubActiveAsync(long id, bool active, CancellationToken cancellationToken)
    {
        return Answer(
            await admin.SetHubActiveAsync(id, active, cancellationToken),
            active ? "Hub open again." : "Hub closed. It takes no parcel and serves no zone.");
    }

    public async Task<IActionResult> OnPostAddZoneAsync(
        string? code,
        string? name,
        long hubId,
        string? city,
        bool isSuburb,
        string? division,
        CancellationToken cancellationToken)
    {
        var added = await admin.AddZoneAsync(new NewZone(code, name, hubId, city, isSuburb, division), cancellationToken);

        return Answer(added, $"{name} is on the map. Add its areas next.", ("/Admin/Coverage?tab=Areas", "Add its areas"));
    }

    public async Task<IActionResult> OnPostEditZoneAsync(
        long id,
        string? code,
        string? name,
        long hubId,
        string? city,
        bool isSuburb,
        string? division,
        CancellationToken cancellationToken)
    {
        var changed = await admin.EditZoneAsync(id, new NewZone(code, name, hubId, city, isSuburb, division), cancellationToken);

        return Answer(changed, "Zone saved. It prices parcels booked from now on.");
    }

    public async Task<IActionResult> OnPostZoneActiveAsync(long id, bool active, CancellationToken cancellationToken)
    {
        return Answer(
            await admin.SetZoneActiveAsync(id, active, cancellationToken),
            active ? "Zone back on the map." : "Zone taken off the map.");
    }

    public async Task<IActionResult> OnPostAddAreaAsync(string? name, long zoneId, CancellationToken cancellationToken)
    {
        return Answer(await admin.AddAreaAsync(name, zoneId, cancellationToken), $"{name} is on the map. Merchants can book to it now.");
    }

    public async Task<IActionResult> OnPostEditAreaAsync(long id, string? name, long zoneId, CancellationToken cancellationToken)
    {
        return Answer(await admin.EditAreaAsync(id, name, zoneId, cancellationToken), "Area saved.");
    }

    public async Task<IActionResult> OnPostAreaActiveAsync(long id, bool active, CancellationToken cancellationToken)
    {
        return Answer(
            await admin.SetAreaActiveAsync(id, active, cancellationToken),
            active ? "Area back on the map." : "Area taken off the map. No new address can be booked to it.");
    }

    /// <summary>Back to the tab, with what happened; <paramref name="next"/> is the step the toast offers after a success.</summary>
    private IActionResult Answer(Result result, string done, (string Link, string Text)? next = null)
    {
        if (result.IsFailure && result.Error!.Type == ErrorType.NotFound)
        {
            return NotFound();
        }

        TempData[result.IsSuccess ? "Done" : "Problem"] = result.IsSuccess ? done : result.Error!.Message;
        if (result.IsSuccess && next is { } step)
        {
            TempData["DoneLink"] = step.Link;
            TempData["DoneLinkText"] = step.Text;
        }

        return RedirectToPage(new { tab = Tab, zoneId = ZoneId });
    }

    private async Task LoadAsync(CancellationToken cancellationToken)
    {
        switch (Tab)
        {
            case CoverageTab.Hubs:
                Hubs = await admin.HubsAsync(cancellationToken);
                break;
            case CoverageTab.Zones:
                Hubs = await admin.HubsAsync(cancellationToken);
                Zones = await admin.ZonesAsync(cancellationToken);
                break;
            case CoverageTab.Areas:
                Zones = await admin.ZonesAsync(cancellationToken);
                Areas = await admin.AreasAsync(ZoneId, cancellationToken);
                break;
            default:
                Map = await coverage.ListAsync(cancellationToken);
                break;
        }
    }
}
