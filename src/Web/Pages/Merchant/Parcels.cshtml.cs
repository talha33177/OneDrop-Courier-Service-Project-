using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Application.Parcels.Browse;

namespace Web.Pages.Merchant;

/// <summary>The merchant's parcels: tabs by stage, search, a date range, and label printing for the ones ticked.</summary>
public class ParcelsModel(ParcelListHandler parcels) : PageModel
{
    [BindProperty(SupportsGet = true)]
    public ParcelTab Tab { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? Search { get; set; }

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

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        List = await parcels.ListAsync(Query(PageNumber, 25), cancellationToken);
    }

    private ParcelQuery Query(int page, int size)
    {
        return new ParcelQuery { Tab = Tab, Search = Search, From = From, To = To, Late = Late, Page = page, PageSize = size };
    }
}
