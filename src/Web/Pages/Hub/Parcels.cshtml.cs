using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Application.Abstractions;
using Application.Hubs;
using Application.Parcels.Browse;

namespace Web.Pages.Hub;

/// <summary>Every parcel of the courier, for its staff: tabs by stage, search, a merchant or hub, a date range, late ones.</summary>
public class ParcelsModel(ParcelListHandler parcels, HubDirectory hubs, IAppDbContext db) : PageModel
{
    [BindProperty(SupportsGet = true)]
    public ParcelTab Tab { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? Search { get; set; }

    [BindProperty(SupportsGet = true)]
    public long? MerchantId { get; set; }

    [BindProperty(SupportsGet = true, Name = "hubId")]
    public long? HubId { get; set; }

    [BindProperty(SupportsGet = true)]
    public DateOnly? From { get; set; }

    [BindProperty(SupportsGet = true)]
    public DateOnly? To { get; set; }

    /// <summary>Only parcels still on their way after the day they were due.</summary>
    [BindProperty(SupportsGet = true)]
    public bool Late { get; set; }

    // "p": "page" is the route value Razor Pages names the page by, so it never binds
    [BindProperty(SupportsGet = true, Name = "p")]
    public int PageNumber { get; set; } = 1;

    public ParcelList List { get; private set; } = null!;

    public IReadOnlyList<HubItem> Hubs { get; private set; } = [];

    public IReadOnlyList<(long Id, string Name)> Merchants { get; private set; } = [];

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        List = await parcels.ListAsync(
            new ParcelQuery { Tab = Tab, Search = Search, MerchantId = MerchantId, HubId = HubId, From = From, To = To, Late = Late, Page = PageNumber },
            cancellationToken);
        Hubs = await hubs.ListAsync(cancellationToken);
        Merchants = [.. (await db.Merchants.OrderBy(m => m.Name).Select(m => new { m.Id, m.Name }).ToListAsync(cancellationToken)).Select(m => (m.Id, m.Name))];
    }
}
