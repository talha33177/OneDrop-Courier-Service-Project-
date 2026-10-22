using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.RateLimiting;
using Application.Abstractions;
using Application.Platform.Accounts;

namespace Web.Pages.Account;

/// <summary>Chooses a new password with the link from an email. The link's token is checked when the form is posted.</summary>
[EnableRateLimiting("public")]
public class ResetPasswordModel(PasswordResetHandler reset, ITenantContext tenantContext) : PageModel
{
    public string Where => tenantContext.Tenant?.Name ?? "the platform";

    [BindProperty(SupportsGet = true, Name = "c")]
    public string? Code { get; set; }

    public string? Problem { get; private set; }

    public IActionResult OnGet()
    {
        return tenantContext.HasTenant ? Page() : NotFound();
    }

    public async Task<IActionResult> OnPostAsync(string? password, string? confirm, CancellationToken cancellationToken)
    {
        if (!tenantContext.HasTenant)
        {
            return NotFound();
        }

        if (password != confirm)
        {
            Problem = "The two passwords are not the same.";

            return Page();
        }

        var changed = await reset.ResetAsync(Code, password, cancellationToken);
        if (changed.IsFailure)
        {
            Problem = changed.Error!.Message;

            return Page();
        }

        TempData["Done"] = "Your new password is set. Sign in with it now.";

        return RedirectToPage("/Account/Login");
    }
}
