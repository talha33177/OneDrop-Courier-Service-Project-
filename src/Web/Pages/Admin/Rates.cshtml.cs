using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Application.Pricing.Rates;
using Domain.Pricing;

namespace Web.Pages.Admin;

/// <summary>The courier's rate card: the admin changes what parcels booked from now on cost. Delivery types are priced on their own page.</summary>
public class RatesModel(RatesHandler rates) : PageModel
{
    public IReadOnlyList<RateRow> Rates { get; private set; } = [];

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        Rates = await rates.ListAsync(cancellationToken);
    }

    public async Task<IActionResult> OnPostAsync(
        ServiceArea area,
        decimal includedKg,
        decimal baseCharge,
        decimal extraKgCharge,
        decimal codChargePercent,
        decimal returnCharge,
        int? deliveryDays,
        CancellationToken cancellationToken)
    {
        var changed = await rates.ChangeAsync(
            area,
            new RateValues((int)Math.Round(includedKg * 1000m), baseCharge, extraKgCharge, codChargePercent, returnCharge, deliveryDays),
            cancellationToken);
        TempData[changed.IsSuccess ? "Done" : "Problem"] = changed.IsSuccess
            ? $"{area.DisplayName()} rate saved. Parcels booked from now on use it."
            : changed.Error!.Message;

        return RedirectToPage();
    }
}
