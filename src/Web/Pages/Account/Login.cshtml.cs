using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Application.Abstractions;
using Infrastructure.Identity;

namespace Web.Pages.Account;

/// <summary>
/// Sign in, for merchants, hub staff, riders and admins. Users are filtered to the current host's tenant, so a courier's
/// site only finds its own logins and the bare domain only finds platform staff.
/// </summary>
public class LoginModel(
    SignInManager<AppUser> signInManager,
    ITenantContext tenantContext) : PageModel
{
    [BindProperty]
    public InputModel Input { get; set; } = new();

    public string Where => tenantContext.Tenant?.Name ?? "the OneDrop platform";

    public bool OnCourierSite => tenantContext.HasTenant;

    public async Task<IActionResult> OnPostAsync(string? returnUrl)
    {
        if (!ModelState.IsValid)
        {
            return Page();
        }

        var result = await signInManager.PasswordSignInAsync(
            Input.Email.Trim(),
            Input.Password,
            Input.RememberMe,
            lockoutOnFailure: true);
        if (result.IsLockedOut)
        {
            ModelState.AddModelError("", "Too many attempts. Try again in a few minutes.");
            return Page();
        }

        if (!result.Succeeded)
        {
            ModelState.AddModelError("", $"The email or password is not right for {Where}.");
            return Page();
        }

        // Hub staff choose the hub they work at after each sign-in, never inherit someone else's choice
        Response.Cookies.Delete(Hub.RememberHub.Cookie);

        return LocalRedirect(Url.IsLocalUrl(returnUrl) ? returnUrl : "/");
    }

    public class InputModel
    {
        [Required(ErrorMessage = "Enter your email.")]
        [EmailAddress(ErrorMessage = "Enter a valid email address.")]
        public string Email { get; set; } = "";

        [Required(ErrorMessage = "Enter your password.")]
        [DataType(DataType.Password)]
        public string Password { get; set; } = "";

        [Display(Name = "Keep me signed in")]
        public bool RememberMe { get; set; }
    }
}
