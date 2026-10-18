using System.ComponentModel.DataAnnotations;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.RateLimiting;
using Application.Abstractions;
using Application.Merchants.Onboarding;
using Application.Network.ListAreas;
using Domain.Merchants;
using Infrastructure.Identity;

namespace Web.Pages.Account;

/// <summary>
/// A shop signs up as a merchant: business details, a login and where parcels are picked up. When an email can reach
/// it, a 6-digit code is emailed first and the account is made only once the code is typed, so the courier knows the
/// address is real. The form waits in the page meanwhile, sealed and good for half an hour, so nothing is stored for
/// a shop that never finishes. The account waits for the courier's approval before it can book; the merchant is
/// signed in at once and sees that on its dashboard.
/// </summary>
[EnableRateLimiting("public")]
public class RegisterModel(
    MerchantOnboarding onboarding,
    SignUpCodes codes,
    ListAreasHandler areas,
    SignInManager<AppUser> signInManager,
    IDataProtectionProvider protection,
    ITenantContext tenantContext) : PageModel
{
    private static readonly TimeSpan FormLifetime = TimeSpan.FromMinutes(30);

    [BindProperty]
    public InputModel Input { get; set; } = new();

    public IReadOnlyList<AreaItem> Areas { get; private set; } = [];

    public string? Problem { get; private set; }

    /// <summary>The filled-in form, sealed, while the shop types its code; null on the form itself.</summary>
    public string? Pending { get; private set; }

    public string? Notice { get; private set; }

    public bool CodeRequired => codes.Required;

    public string Courier => tenantContext.Tenant?.Name ?? "";

    private ITimeLimitedDataProtector Protector => protection.CreateProtector("Courier.SignUp.Form").ToTimeLimitedDataProtector();

    public async Task<IActionResult> OnGetAsync(CancellationToken cancellationToken)
    {
        if (!tenantContext.HasTenant)
        {
            return NotFound();
        }

        Areas = await areas.HandleAsync(cancellationToken);

        return Page();
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        if (!tenantContext.HasTenant)
        {
            return NotFound();
        }

        if (!ModelState.IsValid)
        {
            Areas = await areas.HandleAsync(cancellationToken);

            return Page();
        }

        if (!codes.Required)
        {
            return await CreateAsync(cancellationToken);
        }

        var checkedForm = await onboarding.CheckAsync(Spec(), cancellationToken);
        var sent = checkedForm.IsSuccess ? await codes.SendAsync(Input.Email, Input.Owner, cancellationToken) : checkedForm;
        if (sent.IsFailure)
        {
            Problem = sent.Error!.Message;
            Areas = await areas.HandleAsync(cancellationToken);

            return Page();
        }

        Pending = Protector.Protect(JsonSerializer.Serialize(Input), FormLifetime);

        return Page();
    }

    public async Task<IActionResult> OnPostVerifyAsync(string? pending, string? code, CancellationToken cancellationToken)
    {
        if (!tenantContext.HasTenant)
        {
            return NotFound();
        }

        if (!Unseal(pending))
        {
            return await ExpiredAsync(cancellationToken);
        }

        var verified = await codes.CheckAsync(Input.Email, code, cancellationToken);
        if (verified.IsFailure)
        {
            Problem = verified.Error!.Message;
            Pending = pending;

            return Page();
        }

        return await CreateAsync(cancellationToken);
    }

    public async Task<IActionResult> OnPostResendAsync(string? pending, CancellationToken cancellationToken)
    {
        if (!tenantContext.HasTenant)
        {
            return NotFound();
        }

        if (!Unseal(pending))
        {
            return await ExpiredAsync(cancellationToken);
        }

        var sent = await codes.SendAsync(Input.Email, Input.Owner, cancellationToken);
        Problem = sent.IsFailure ? sent.Error!.Message : null;
        Notice = sent.IsSuccess ? "A new code is on its way. The one before no longer works." : null;
        Pending = pending;

        return Page();
    }

    private async Task<IActionResult> CreateAsync(CancellationToken cancellationToken)
    {
        var created = await onboarding.CreateAsync(Spec(), approved: false, cancellationToken);
        if (created.IsFailure)
        {
            Problem = created.Error!.Message;
            Areas = await areas.HandleAsync(cancellationToken);

            return Page();
        }

        var user = await signInManager.UserManager.FindByNameAsync(Input.Email.Trim());
        await signInManager.SignInAsync(user!, isPersistent: false);

        return RedirectToPage("/Merchant/Index");
    }

    private NewMerchant Spec()
    {
        return new NewMerchant(
            new MerchantProfile(Input.Business, Input.Owner, Input.Phone, Input.Email, Input.Address),
            Input.Email,
            Input.Password,
            Input.PickupAreaId,
            Input.PickupAddress);
    }

    /// <summary>Reads the sealed form back into <see cref="Input"/>; false when it was tampered with or is too old.</summary>
    private bool Unseal(string? pending)
    {
        try
        {
            Input = JsonSerializer.Deserialize<InputModel>(Protector.Unprotect(pending ?? "")) ?? new InputModel();

            return Input.Email.Length > 0;
        }
        catch (Exception exception) when (exception is CryptographicException or JsonException)
        {
            return false;
        }
    }

    private async Task<IActionResult> ExpiredAsync(CancellationToken cancellationToken)
    {
        Input = new InputModel();
        ModelState.Clear();
        Problem = "That took too long, so the form was cleared. Fill it in again and we will send a new code.";
        Areas = await areas.HandleAsync(cancellationToken);

        return Page();
    }

    public class InputModel
    {
        [Required(ErrorMessage = "Enter your business or page name.")]
        [StringLength(200)]
        public string Business { get; set; } = "";

        [Required(ErrorMessage = "Enter the owner's name.")]
        [StringLength(200)]
        public string Owner { get; set; } = "";

        [Required(ErrorMessage = "Enter a mobile number.")]
        public string Phone { get; set; } = "";

        [Required(ErrorMessage = "Enter your email.")]
        [EmailAddress(ErrorMessage = "Enter a valid email address.")]
        public string Email { get; set; } = "";

        [Required(ErrorMessage = "Choose a password.")]
        [StringLength(100, MinimumLength = 8, ErrorMessage = "Use at least 8 characters.")]
        [DataType(DataType.Password)]
        public string Password { get; set; } = "";

        [Required(ErrorMessage = "Enter your business address.")]
        [StringLength(500)]
        public string Address { get; set; } = "";

        [Required(ErrorMessage = "Choose the area we pick up from.")]
        public long? PickupAreaId { get; set; }

        [Required(ErrorMessage = "Enter the pickup address.")]
        [StringLength(500)]
        public string PickupAddress { get; set; } = "";
    }
}
