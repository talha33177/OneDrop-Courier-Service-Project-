using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Application.Hubs.Cash;

namespace Web.Pages.Admin;

/// <summary>
/// Finance's view of the hubs' cash: what each hub holds, the bank deposits to confirm or refuse, the latest answered,
/// and the riders' shortfalls no hub manager has accepted yet.
/// </summary>
public class CashModel(HubCashHandler cash) : PageModel
{
    public CourierCash Data { get; private set; } = null!;

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        Data = await cash.CourierAsync(cancellationToken);
    }

    public async Task<IActionResult> OnPostConfirmAsync(string number, CancellationToken cancellationToken)
    {
        var confirmed = await cash.ConfirmAsync(number, cancellationToken);
        TempData[confirmed.IsSuccess ? "Done" : "Problem"] = confirmed.IsSuccess
            ? $"Deposit {number} confirmed."
            : confirmed.Error!.Message;

        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostRefuseAsync(string number, string? reason, CancellationToken cancellationToken)
    {
        var refused = await cash.RefuseAsync(number, reason, cancellationToken);
        TempData[refused.IsSuccess ? "Done" : "Problem"] = refused.IsSuccess
            ? $"Deposit {number} refused. The cash counts as the hub's again until it is recorded right."
            : refused.Error!.Message;

        return RedirectToPage();
    }
}
