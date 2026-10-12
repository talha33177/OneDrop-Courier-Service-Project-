using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Application.Abstractions;
using Infrastructure.Persistence;

namespace Infrastructure.MultiTenancy;

/// <summary>
/// The platform's list of tenants, cached for a few minutes because every request needs it. Platform.Tenant
/// has no tenant filter, so a fresh scope with no tenant set can read it.
/// </summary>
public class TenantCatalog(IServiceScopeFactory scopeFactory, IMemoryCache cache) : ITenantCatalog
{
    private const string CacheKey = "tenants";
    private static readonly TimeSpan CacheFor = TimeSpan.FromMinutes(5);

    public async Task<TenantInfo?> FindBySlugAsync(string slug, CancellationToken cancellationToken = default)
    {
        var tenants = await ListAsync(cancellationToken);

        return tenants.FirstOrDefault(t => t.Slug.Equals(slug, StringComparison.OrdinalIgnoreCase));
    }

    public async Task<TenantInfo?> FindByIdAsync(long id, CancellationToken cancellationToken = default)
    {
        var tenants = await ListAsync(cancellationToken);

        return tenants.FirstOrDefault(t => t.Id == id);
    }

    public async Task<IReadOnlyList<TenantInfo>> ListAsync(CancellationToken cancellationToken = default)
    {
        return await cache.GetOrCreateAsync(CacheKey, async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = CacheFor;
            await using var scope = scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            return (IReadOnlyList<TenantInfo>)await db.Tenants
                .Where(t => !t.Archived)
                .OrderBy(t => t.Name)
                .Select(t => new TenantInfo(
                    t.Id,
                    t.Name,
                    t.Slug,
                    t.TimeZone,
                    t.CurrencyCode,
                    t.SmsSenderName,
                    t.SupportPhone,
                    t.MaxDeliveryAttempts,
                    t.RiderReturnTime,
                    t.MaxBusinessesPerAccount,
                    t.PickupCutoff,
                    t.PickupTime))
                .AsNoTracking()
                .ToListAsync(cancellationToken);
        }) ?? [];
    }
}
