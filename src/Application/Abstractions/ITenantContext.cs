namespace Application.Abstractions;

/// <summary>A tenant and its settings, as cached by <see cref="ITenantCatalog"/>. Its rate card is read from the database.</summary>
public sealed record TenantInfo(
    long Id,
    string Name,
    string Slug,
    string TimeZone,
    string CurrencyCode,
    string SmsSenderName,
    string SupportPhone,
    int MaxDeliveryAttempts,
    TimeOnly? RiderReturnTime,
    int? MaxBusinessesPerAccount,
    TimeOnly? PickupCutoff,
    TimeOnly? PickupTime);

/// <summary>
/// The tenant the current request or job runs for. Resolved once per request from the subdomain or the merchant's API
/// key; background jobs receive it as a parameter. When no tenant is set, tenant-owned queries return nothing.
/// </summary>
public interface ITenantContext
{
    TenantInfo? Tenant { get; }

    long? TenantId => Tenant?.Id;

    bool HasTenant => Tenant is not null;
}

/// <summary>Looks tenants up by subdomain or id. Results are cached; tenants change rarely.</summary>
public interface ITenantCatalog
{
    Task<TenantInfo?> FindBySlugAsync(string slug, CancellationToken cancellationToken = default);

    Task<TenantInfo?> FindByIdAsync(long id, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<TenantInfo>> ListAsync(CancellationToken cancellationToken = default);
}
