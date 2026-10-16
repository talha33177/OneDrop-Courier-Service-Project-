using Microsoft.EntityFrameworkCore;
using Application.Abstractions;
using Application.Common;
using Domain.Common;
using Domain.Platform;

namespace Application.Platform.Settings;

/// <summary>
/// The courier's own settings, kept by its admin: the name and hotline merchants and recipients see, the SMS sender,
/// delivery attempts, businesses per account and the day's times. Every page and job reads them from the cached
/// tenant, so a change clears that cache and applies from the next request on.
/// </summary>
public class CourierSettingsHandler(IAppDbContext db, ITenantContext tenantContext, ITenantCatalog catalog)
{
    public TenantSettings Current()
    {
        var tenant = tenantContext.Require();

        return new TenantSettings(
            tenant.Name,
            tenant.SupportPhone,
            tenant.SmsSenderName,
            tenant.MaxDeliveryAttempts,
            tenant.MaxBusinessesPerAccount,
            tenant.RiderReturnTime,
            tenant.PickupTime,
            tenant.StuckAfterDays);
    }

    public async Task<Result> ChangeAsync(TenantSettings settings, CancellationToken cancellationToken = default)
    {
        // Platform.Tenant has no tenant filter: the row is picked by the request's own tenant, never by anything posted
        var id = tenantContext.Require().Id;
        var tenant = await db.Tenants.SingleAsync(t => t.Id == id, cancellationToken);
        var changed = tenant.ChangeSettings(settings);
        if (changed.IsFailure)
        {
            return changed;
        }

        await db.SaveChangesAsync(cancellationToken);
        catalog.Forget();

        return changed;
    }
}
