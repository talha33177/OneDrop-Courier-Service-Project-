using Microsoft.AspNetCore.Mvc;
using Application.Hubs.Cash;
using Application.Hubs.HubOverview;
using Domain.Common;
using Domain.Staff;

namespace Web.Pages.Hub;

/// <summary>
/// The hub's cash: what its riders handed in at closing, what the cash desk paid into the bank and what it holds now.
/// The cash desk records each deposit with its slip; the hub manager accepts each rider's shortfall and says what was
/// done about it.
/// </summary>
public class CashModel(HubOverviewHandler hubs, HubCashHandler cash) : HubPage(hubs)
{
    public HubCash? Data { get; private set; }

    public bool MayDeposit => Rights.May(StaffPermissions.CashDesk);

    public bool MayAccept => Rights.May(StaffPermissions.CashShortfalls);

    public async Task<IActionResult> OnGetAsync(CancellationToken cancellationToken)
    {
        if (!await LoadHubAsync(cancellationToken))
        {
            return NotFound();
        }

        if (Hub is not null)
        {
            Data = await cash.HubAsync(Hub.Code, cancellationToken);
        }

        return Page();
    }

    public async Task<IActionResult> OnPostDepositAsync(
        decimal amount,
        string? bank,
        string? slip,
        DateOnly depositedOn,
        CancellationToken cancellationToken)
    {
        if (!await LoadHubAsync(cancellationToken) || Hub is null)
        {
            return NotFound();
        }

        var recorded = await cash.DepositAsync(Hub.Code, amount, bank, slip, depositedOn, cancellationToken);
        TempData[recorded.IsSuccess ? "Done" : "Problem"] = recorded.IsSuccess
            ? $"Deposit {recorded.Value} of ৳{amount:N0} recorded. Finance confirms it once it shows in the bank."
            : recorded.Error!.Message;

        return RedirectToPage(new { hub = Hub.Code });
    }

    public async Task<IActionResult> OnPostAcceptAsync(long id, string? note, CancellationToken cancellationToken)
    {
        if (!await LoadHubAsync(cancellationToken) || Hub is null)
        {
            return NotFound();
        }

        var accepted = await cash.AcceptShortAsync(Hub.Code, id, note, cancellationToken);
        if (accepted.IsFailure && accepted.Error!.Type == ErrorType.NotFound)
        {
            return NotFound();
        }

        TempData[accepted.IsSuccess ? "Done" : "Problem"] = accepted.IsSuccess ? "Shortfall accepted." : accepted.Error!.Message;

        return RedirectToPage(new { hub = Hub.Code });
    }
}
