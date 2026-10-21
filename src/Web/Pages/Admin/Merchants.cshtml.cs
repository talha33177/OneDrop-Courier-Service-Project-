using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Application.Merchants.Admin;
using Domain.Merchants;

namespace Web.Pages.Admin;

/// <summary>The courier's merchants, by status, with what they send and what they are owed.</summary>
public class MerchantsModel(AdminMerchantsHandler merchants) : PageModel
{
    [BindProperty(SupportsGet = true)]
    public MerchantStatus? Status { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? Search { get; set; }

    // "p": "page" is the route value Razor Pages names the page by, so it never binds
    [BindProperty(SupportsGet = true, Name = "p")]
    public int PageNumber { get; set; } = 1;

    public Application.Common.Page<MerchantRow> List { get; private set; } = null!;

    public IReadOnlyList<MerchantRow> Rows => List.Items;

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        List = await merchants.ListAsync(Status, Search, PageNumber, cancellationToken);
    }
}
