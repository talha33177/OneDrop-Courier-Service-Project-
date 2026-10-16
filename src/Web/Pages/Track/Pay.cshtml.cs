using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.RateLimiting;
using Application.Abstractions;
using Application.Payments.OnlinePayments;

namespace Web.Pages.Track;

/// <summary>
/// The recipient pays a parcel's cash on delivery online. Public like tracking, so the amount shows only once the
/// phone number the parcel is for is given; the gateway sends the payer back here with the payment to read.
/// </summary>
[EnableRateLimiting("public")]
public class PayModel(RecipientPaymentsHandler handler, ITenantContext tenantContext) : PageModel
{
    [BindProperty(SupportsGet = true)]
    public string? Code { get; set; }

    [BindProperty]
    public string? Phone { get; set; }

    [BindProperty]
    public string? Email { get; set; }

    /// <summary>The payment the gateway just sent the payer back from.</summary>
    [BindProperty(Name = "payment", SupportsGet = true)]
    public string? PaymentNumber { get; set; }

    public ParcelToPay? Parcel { get; private set; }

    public RecipientPaymentView? Payment { get; private set; }

    public string? Problem { get; private set; }

    public async Task<IActionResult> OnGetAsync(CancellationToken cancellationToken)
    {
        if (!tenantContext.HasTenant)
        {
            return NotFound();
        }

        if (PaymentNumber is not null)
        {
            Payment = await handler.PaymentAsync(Code, PaymentNumber, cancellationToken);
        }

        return Page();
    }

    public async Task<IActionResult> OnPostCheckAsync(CancellationToken cancellationToken)
    {
        if (!tenantContext.HasTenant)
        {
            return NotFound();
        }

        var found = await handler.GetAsync(Code, Phone, cancellationToken);
        if (found.IsSuccess)
        {
            Parcel = found.Value;
        }
        else
        {
            Problem = found.Error!.Message;
        }

        return Page();
    }

    public async Task<IActionResult> OnPostStartAsync(CancellationToken cancellationToken)
    {
        if (!tenantContext.HasTenant)
        {
            return NotFound();
        }

        var started = await handler.StartAsync(Code, Phone, Email, cancellationToken);
        if (started.IsSuccess)
        {
            return Redirect(started.Value.PaymentPageUrl);
        }

        Problem = started.Error!.Message;
        var found = await handler.GetAsync(Code, Phone, cancellationToken);
        Parcel = found.IsSuccess ? found.Value : null;

        return Page();
    }
}
