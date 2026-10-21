using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Application.Payments.AdminPayouts;
using Application.Payments.OnlinePayments;
using Application.Payments.PayoutRequests;
using Application.Payments.RunPayouts;
using Domain.Payments;

namespace Web.Pages.Admin;

/// <summary>
/// What the courier owes each merchant now and who owes it, merchants asking to be paid now, payouts the gateway
/// refused, the payouts made, a button to run the payouts at once, and what merchants paid online, with the payments the
/// gateway held for a check.
/// </summary>
public class PayoutsModel(
    AdminPayoutsHandler payouts,
    PayoutsJob job,
    OnlinePaymentsHandler online,
    PayoutRequestsHandler requests) : PageModel
{
    [BindProperty(SupportsGet = true)]
    public PayoutStatus? Status { get; set; }

    // "p": "page" is the route value Razor Pages names the page by, so it never binds
    [BindProperty(SupportsGet = true, Name = "p")]
    public int PageNumber { get; set; } = 1;

    public PayoutsOverview Data { get; private set; } = null!;

    public AdminOnlinePayments Online { get; private set; } = null!;

    public IReadOnlyList<PayoutRequestReview> Requests { get; private set; } = [];

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        Data = await payouts.GetAsync(Status, PageNumber, cancellationToken);
        Online = await online.ForCourierAsync(cancellationToken);
        Requests = await requests.OpenAsync(cancellationToken);
    }

    /// <summary>Pays a merchant's request: its balance now, today's too, sent at once.</summary>
    public async Task<IActionResult> OnPostPayRequestAsync(long id, CancellationToken cancellationToken)
    {
        var paid = await requests.PayAsync(id, cancellationToken);
        if (paid.IsFailure)
        {
            TempData["Problem"] = paid.Error!.Message;
        }
        else if (!paid.Value.Sent)
        {
            TempData["Problem"] = $"{paid.Value.Number} was made, but the gateway refused the transfer. It is listed below and tried again every hour.";
        }
        else
        {
            TempData["Done"] = $"{paid.Value.Number} was sent to the merchant.";
            TempData["DoneLink"] = $"/Admin/Payout/{paid.Value.Number}";
            TempData["DoneLinkText"] = "See the invoice";
        }

        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostRefuseRequestAsync(long id, string? answer, CancellationToken cancellationToken)
    {
        var refused = await requests.RefuseAsync(id, answer, cancellationToken);
        TempData[refused.IsSuccess ? "Done" : "Problem"] = refused.IsSuccess
            ? "The merchant reads your answer; its balance goes out with the daily payouts."
            : refused.Error!.Message;

        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostCreditAsync(string? number, CancellationToken cancellationToken)
    {
        var credited = await online.AcceptAsync(number, cancellationToken);
        TempData[credited.IsSuccess ? "Done" : "Problem"] = credited.IsSuccess ? $"{number} was accepted: a merchant's payment is credited to its balance, a recipient's goes to its parcel." : credited.Error!.Message;

        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostRefundedAsync(string? number, string? note, CancellationToken cancellationToken)
    {
        var refunded = await online.RefundAsync(number, note, cancellationToken);
        TempData[refunded.IsSuccess ? "Done" : "Problem"] = refunded.IsSuccess ? $"{number} is recorded as refunded." : refunded.Error!.Message;

        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostRunAsync(CancellationToken cancellationToken)
    {
        var run = await job.PayAsync(cancellationToken);
        var said = run is { Paid: 0, Waiting: 0, Failed: 0 }
            ? "Nothing payable yet. Payouts cover parcels finished up to yesterday; today's are paid from tomorrow."
            : $"{run.Paid} payout{(run.Paid == 1 ? "" : "s")} sent, ৳{run.Amount:N0} in all." +
                (run.Waiting > 0
                    ? $" {run.Waiting} merchant{(run.Waiting == 1 ? "" : "s")} wait: charges more than cash, no payout account, or on hold."
                    : "");
        if (run.Failed > 0)
        {
            TempData["Problem"] = $"{said} The gateway refused {run.Failed}: see why below.";
        }
        else
        {
            TempData["Done"] = said;
        }

        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostSendAsync(string? number, CancellationToken cancellationToken)
    {
        var sent = await job.SendAgainAsync(number, cancellationToken);
        TempData[sent.IsSuccess ? "Done" : "Problem"] = sent.IsSuccess ? $"{number} was sent." : sent.Error!.Message;

        return RedirectToPage();
    }
}
