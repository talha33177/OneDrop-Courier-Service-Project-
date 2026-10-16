using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Application.Merchants.Admin;
using Application.Parcels.Browse;
using Application.Payments.RunPayouts;
using Domain.Common;
using Domain.Merchants;

namespace Web.Pages.Admin;

/// <summary>
/// One merchant for the admin with its whole account: the businesses, moderators, saved payout accounts, payouts and
/// requests; approve, suspend or reactivate, correct its profile and payout account, hold, release or pay its payouts
/// now, and write adjustments on its balance.
/// </summary>
public class MerchantModel(AdminMerchantsHandler merchants, ParcelListHandler parcels, PayoutsJob job) : PageModel
{
    public MerchantView Merchant { get; private set; } = null!;

    public MerchantAccountView Account { get; private set; } = null!;

    public IReadOnlyList<ParcelRow> Recent { get; private set; } = [];

    public async Task<IActionResult> OnGetAsync(long id, CancellationToken cancellationToken)
    {
        var found = await merchants.GetAsync(id, cancellationToken);
        if (found.IsFailure)
        {
            return NotFound();
        }

        Merchant = found.Value;
        Account = (await merchants.AccountAsync(id, cancellationToken)).Value;
        Recent = (await parcels.ListAsync(new ParcelQuery { MerchantId = id, PageSize = 10 }, cancellationToken)).Page.Items;

        return Page();
    }

    public async Task<IActionResult> OnPostApproveAsync(long id, CancellationToken cancellationToken)
    {
        var approved = await merchants.ApproveAsync(id, cancellationToken);
        var waiting = approved.IsSuccess ? (await merchants.ListAsync(MerchantStatus.Pending, null, cancellationToken)).Count : 0;
        if (waiting > 0)
        {
            TempData["DoneLink"] = "/Admin/Merchants?status=Pending";
            TempData["DoneLinkText"] = $"Next sign-up ({waiting} waiting)";
        }

        return Answer(id, approved, "Approved: the merchant can book parcels now.");
    }

    public async Task<IActionResult> OnPostSuspendAsync(long id, CancellationToken cancellationToken)
    {
        return Answer(id, await merchants.SuspendAsync(id, cancellationToken), "Suspended: the merchant cannot book parcels.");
    }

    public async Task<IActionResult> OnPostReactivateAsync(long id, CancellationToken cancellationToken)
    {
        return Answer(id, await merchants.ReactivateAsync(id, cancellationToken), "Reactivated.");
    }

    public async Task<IActionResult> OnPostProfileAsync(
        long id,
        string? name,
        string? owner,
        string? phone,
        string? email,
        string? address,
        CancellationToken cancellationToken)
    {
        var saved = await merchants.EditAsync(id, new MerchantProfile(name, owner, phone, email, address), cancellationToken);

        return Answer(id, saved, "Profile saved.");
    }

    public async Task<IActionResult> OnPostPayoutAsync(
        long id,
        PayoutMethod method,
        string? payoutAccount,
        string? accountName,
        CancellationToken cancellationToken)
    {
        return Answer(id, await merchants.SetPayoutAsync(id, method, payoutAccount, accountName, cancellationToken), "Payout account saved.");
    }

    public async Task<IActionResult> OnPostHoldAsync(long id, string? reason, CancellationToken cancellationToken)
    {
        return Answer(id, await merchants.HoldPayoutsAsync(id, reason, cancellationToken), "Payouts held for the whole account.");
    }

    public async Task<IActionResult> OnPostReleaseAsync(long id, CancellationToken cancellationToken)
    {
        return Answer(id, await merchants.ReleasePayoutsAsync(id, cancellationToken), "Payouts released: the next run pays the balance.");
    }

    public async Task<IActionResult> OnPostPayNowAsync(long id, CancellationToken cancellationToken)
    {
        var paid = await job.PayNowAsync(id, cancellationToken);
        if (paid.IsFailure)
        {
            return Answer(id, paid.Error!, "");
        }

        if (!paid.Value.Sent)
        {
            return Answer(id, Error.Conflict("payout.refused", $"{paid.Value.Number} was made, but the gateway refused it. See why on the payouts page."), "");
        }

        TempData["DoneLink"] = $"/Admin/Payout/{paid.Value.Number}";
        TempData["DoneLinkText"] = $"Open {paid.Value.Number}";

        return Answer(id, Result.Success(), $"{paid.Value.Number} sent.");
    }

    public async Task<IActionResult> OnPostAdjustAsync(long id, string? direction, decimal amount, string? note, CancellationToken cancellationToken)
    {
        var signed = direction == "charge" ? -Math.Abs(amount) : Math.Abs(amount);

        return Answer(id, await merchants.AdjustAsync(id, signed, note, cancellationToken), "Adjustment written. It goes into the next payout.");
    }

    private IActionResult Answer(long id, Result result, string done)
    {
        if (result.IsFailure && result.Error!.Type == ErrorType.NotFound)
        {
            return NotFound();
        }

        TempData[result.IsSuccess ? "Done" : "Problem"] = result.IsSuccess ? done : result.Error!.Message;

        return RedirectToPage(new { id });
    }
}
