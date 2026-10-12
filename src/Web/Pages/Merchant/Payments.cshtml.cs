using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Application.Payments.MerchantPayments;
using Application.Payments.OnlinePayments;
using Application.Payments.PayoutRequests;

namespace Web.Pages.Merchant;

/// <summary>
/// The merchant's money: its balance and how it adds up, what is still to collect, the payouts already made, a request
/// to be paid now that the courier's admin answers, and, when its charges come to more than its cash, a way to pay what
/// it owes online. Coming back from the payment page, <c>?payment=PAY-100001</c> says how that payment went.
/// </summary>
public class PaymentsModel(
    MerchantPaymentsHandler payments,
    PayoutRequestsHandler requests,
    OnlinePaymentsHandler online) : PageModel
{
    public MerchantPayments Data { get; private set; } = null!;

    public MerchantOnlinePayments Online { get; private set; } = null!;

    public MerchantPayoutRequests Requests { get; private set; } = null!;

    /// <summary>The payment the merchant just came back from, as it stands now; null when there is none, or it is another merchant's.</summary>
    public OnlinePaymentRow? Returned { get; private set; }

    public async Task OnGetAsync(string? payment, CancellationToken cancellationToken)
    {
        Returned = string.IsNullOrWhiteSpace(payment) ? null : await online.PaymentAsync(payment, cancellationToken);
        Data = await payments.GetAsync(cancellationToken);
        Online = await online.ForMerchantAsync(cancellationToken);
        Requests = await requests.MineAsync(cancellationToken);
    }

    /// <summary>Asks the courier to pay the balance now instead of the next morning.</summary>
    public async Task<IActionResult> OnPostAskAsync(CancellationToken cancellationToken)
    {
        var asked = await requests.AskAsync(cancellationToken);
        TempData[asked.IsSuccess ? "Done" : "Problem"] = asked.IsSuccess
            ? "Your request is with the courier. You will see the answer here."
            : asked.Error!.Message;

        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostWithdrawAsync(CancellationToken cancellationToken)
    {
        var withdrawn = await requests.WithdrawAsync(cancellationToken);
        TempData[withdrawn.IsSuccess ? "Done" : "Problem"] = withdrawn.IsSuccess
            ? "Your request was taken back. Your balance goes out with the daily payouts."
            : withdrawn.Error!.Message;

        return RedirectToPage();
    }

    /// <summary>Opens a payment of what the merchant owes now and sends it to the gateway's payment page.</summary>
    public async Task<IActionResult> OnPostPayOnlineAsync(CancellationToken cancellationToken)
    {
        var started = await online.StartAsync(cancellationToken);
        if (started.IsFailure)
        {
            TempData["Problem"] = started.Error!.Message;

            return RedirectToPage();
        }

        return Redirect(started.Value.PaymentPageUrl);
    }
}
