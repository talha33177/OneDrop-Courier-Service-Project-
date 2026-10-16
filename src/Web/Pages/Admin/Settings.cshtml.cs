using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Application.Abstractions;
using Application.Common;
using Application.Platform.Settings;
using Domain.Platform;

namespace Web.Pages.Admin;

/// <summary>The courier's own settings: what merchants and recipients see, delivery attempts and the day's times.</summary>
public class SettingsModel(CourierSettingsHandler settings, ITenantContext tenantContext) : PageModel
{
    public TenantSettings Settings { get; private set; } = null!;

    public TenantInfo Tenant => tenantContext.Require();

    public void OnGet()
    {
        Settings = settings.Current();
    }

    public async Task<IActionResult> OnPostAsync(
        string? name,
        string? supportPhone,
        string? smsSenderName,
        int maxDeliveryAttempts,
        int? maxBusinessesPerAccount,
        TimeOnly? riderReturnTime,
        TimeOnly? pickupTime,
        int? stuckAfterDays,
        CancellationToken cancellationToken)
    {
        var posted = new TenantSettings(
            name, supportPhone, smsSenderName, maxDeliveryAttempts, maxBusinessesPerAccount, riderReturnTime, pickupTime, stuckAfterDays);
        var changed = await settings.ChangeAsync(posted, cancellationToken);
        if (changed.IsFailure)
        {
            // Shown again as typed, so one wrong field does not cost the others
            TempData["Problem"] = changed.Error!.Message;
            Settings = posted;

            return Page();
        }

        TempData["Done"] = "Settings saved. Merchants, riders and recipients see them from now on.";

        return RedirectToPage();
    }
}
