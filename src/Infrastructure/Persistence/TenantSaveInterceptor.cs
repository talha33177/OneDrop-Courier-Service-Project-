using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Application.Abstractions;
using Domain.Common;
using Domain.Parcels;

namespace Infrastructure.Persistence;

/// <summary>
/// Isolation layer 2. Before every save:
/// - new tenant-owned rows are stamped with the current tenant (a row that already names another tenant is refused);
/// - changes and deletes of another tenant's rows are refused, as is changing a row's TenantId;
/// - Created, UpdatedOn and UpdatedId are stamped, and a parcel's MovedOn when its status or place changed.
/// A refused write throws: it is a bug, never a business outcome.
/// </summary>
public class TenantSaveInterceptor(ITenantContext tenantContext, ICurrentUser currentUser, TimeProvider time)
    : SaveChangesInterceptor
{
    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        Stamp(eventData.Context);

        return base.SavingChanges(eventData, result);
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        Stamp(eventData.Context);

        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    private void Stamp(DbContext? context)
    {
        if (context is null)
        {
            return;
        }

        var now = time.GetUtcNow().UtcDateTime;

        // Created is DATETIME2(0), which SQL Server rounds; truncating here keeps the saved entity equal to the row
        var createdNow = now.AddTicks(-(now.Ticks % TimeSpan.TicksPerSecond));
        var userId = currentUser.UserId;

        foreach (var entry in context.ChangeTracker.Entries<ITenantOwned>())
        {
            GuardTenant(entry);
        }

        foreach (var entry in context.ChangeTracker.Entries<AuditedEntity>())
        {
            switch (entry.State)
            {
                case EntityState.Added:
                    entry.Property(nameof(AuditedEntity.Created)).CurrentValue = createdNow;
                    entry.Property(nameof(AuditedEntity.UpdatedOn)).CurrentValue = now;
                    entry.Property(nameof(AuditedEntity.UpdatedId)).CurrentValue = userId;
                    break;
                case EntityState.Modified:
                    entry.Property(nameof(AuditedEntity.Created)).IsModified = false;
                    entry.Property(nameof(AuditedEntity.UpdatedOn)).CurrentValue = now;
                    entry.Property(nameof(AuditedEntity.UpdatedId)).CurrentValue = userId;
                    break;
            }
        }

        // A parcel has moved when its status or its place changed; a note in its history leaves the time as it was
        foreach (var entry in context.ChangeTracker.Entries<Parcel>())
        {
            if (entry.State == EntityState.Added || (entry.State == EntityState.Modified && MovedProperties.Any(p => entry.Property(p).IsModified)))
            {
                entry.Property(nameof(Parcel.MovedOn)).CurrentValue = now;
            }
        }
    }

    private static readonly string[] MovedProperties =
        [nameof(Parcel.Status), nameof(Parcel.CurrentHubId), nameof(Parcel.TransferToHubId), nameof(Parcel.RiderId)];

    private void GuardTenant(EntityEntry<ITenantOwned> entry)
    {
        if (entry.State is EntityState.Unchanged or EntityState.Detached)
        {
            return;
        }

        var tenantId = tenantContext.TenantId ?? throw new InvalidOperationException(
            $"Cannot save {entry.Metadata.ClrType.Name}: no tenant is set for this request or job.");
        var property = entry.Property(nameof(ITenantOwned.TenantId));

        if (entry.State == EntityState.Added)
        {
            var assigned = (long)property.CurrentValue!;
            if (assigned == 0)
            {
                property.CurrentValue = tenantId;
            }
            else if (assigned != tenantId)
            {
                throw CrossTenant(entry, assigned, tenantId);
            }

            return;
        }

        var owner = (long)property.OriginalValue!;
        if (owner != tenantId || property.IsModified)
        {
            throw CrossTenant(entry, owner, tenantId);
        }
    }

    private static InvalidOperationException CrossTenant(
        EntityEntry entry,
        long owner,
        long current)
    {
        return new InvalidOperationException(
            $"Refused to write {entry.Metadata.ClrType.Name} of tenant {owner} while running for tenant {current}.");
    }
}
