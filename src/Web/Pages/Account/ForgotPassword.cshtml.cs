using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.RateLimiting;
using Application.Abstractions;
using Application.Platform.Accounts;

namespace Web.Pages.Account;

/// <summary>
/// Asks for a link to choose a new password. The same answer whether or not the email has a login here, and rate
/// limited per address like the other public pages, so it can neither list who has a login nor flood a mailbox.
/// </summary>
[EnableRateLimiting("public")]
public class ForgotPasswordModel(PasswordResetHandler reset, ITenantContext tenantContext) : PageModel
{
    public bool OnCourierSite => tenantContext.HasTenant;

    public string Where => tenantContext.Tenant?.Name ?? "the platform";

    public bool Sent { get; private set; }

    [BindProperty]
    public string? Email { get; set; }

    public void OnGet()
    {
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        if (!OnCourierSite)
        {
            return NotFound();
        }

        await reset.RequestAsync(Email, cancellationToken);
        Sent = true;

        return Page();
    }
}
