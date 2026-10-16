using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Application.Abstractions;
using Application.Payments.OnlinePayments;

namespace Web.Payments;

/// <summary>
/// Where the payment gateway sends the payer back (<c>/pay/{transaction}/success|fail|cancel</c>) and posts its own
/// notice (<c>/pay/notice</c>), on the courier's own host. Open to anyone and without an antiforgery token, because the
/// gateway's site posts here and the merchant's sign-in does not travel with that post; that is safe because nothing
/// posted is believed: the handler asks the gateway itself. A merchant then lands on its payments page, a recipient on
/// the parcel's pay page, and the page reads the answer.
/// </summary>
[AllowAnonymous]
[IgnoreAntiforgeryToken]
[EnableRateLimiting("public")]
[Route("pay")]
public class PaymentReturnController(OnlinePaymentsHandler payments, ITenantContext tenantContext) : Controller
{
    [AcceptVerbs("GET", "POST")]
    [Route("{transactionId}/{outcome:regex(^(success|fail|cancel)$)}")]
    public async Task<IActionResult> Return(string transactionId, string outcome, CancellationToken cancellationToken)
    {
        if (tenantContext.Tenant is null)
        {
            return NotFound();
        }

        var sent = await SentAsync(cancellationToken);
        var returned = await payments.ReturnedAsync(transactionId, outcome, sent("val_id"), cancellationToken);

        return returned switch
        {
            null => NotFound(),
            { TrackingCode: { } code } => Redirect($"/Track/Pay/{Uri.EscapeDataString(code)}?payment={Uri.EscapeDataString(returned.Number)}"),
            _ => Redirect($"/Merchant/Payments?payment={Uri.EscapeDataString(returned.Number)}")
        };
    }

    [HttpPost("notice")]
    public async Task<IActionResult> Notice(CancellationToken cancellationToken)
    {
        if (tenantContext.Tenant is null)
        {
            return NotFound();
        }

        var sent = await SentAsync(cancellationToken);
        await payments.NoticeAsync(sent("tran_id"), sent("val_id"), cancellationToken);

        return Ok();
    }

    /// <summary>The fields the gateway sent, from the form it posted or from the address; a missing or empty one is null.</summary>
    private async Task<Func<string, string?>> SentAsync(CancellationToken cancellationToken)
    {
        var form = Request.HasFormContentType ? await Request.ReadFormAsync(cancellationToken) : null;

        return name =>
        {
            var value = form is not null && form.TryGetValue(name, out var posted) ? posted.ToString() : Request.Query[name].ToString();

            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        };
    }
}
