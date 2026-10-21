using System.Linq.Expressions;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Application.Abstractions;
using Application.Notifications;
using Domain.Common;
using Domain.Delivery;
using Domain.Merchants;
using Domain.Network;
using Domain.Notifications;
using Domain.Parcels;
using Domain.Payments;
using Domain.Platform;
using Domain.Pricing;
using Domain.Staff;
using Infrastructure.Identity;

namespace Infrastructure.Persistence;

/// <summary>
/// The one DbContext. It maps onto the schema published from src/Database and never creates or
/// migrates anything itself.
///
/// Isolation layer 1 lives here: every <see cref="ITenantOwned"/> entity gets the named filter
/// <see cref="TenantFilter"/> and every <see cref="IMerchantOwned"/> entity the named filter
/// <see cref="MerchantFilter"/>. The filters read <see cref="CurrentTenantId"/> and
/// <see cref="CurrentMerchantId"/> when each query runs, so one cached model serves every tenant. Code that
/// genuinely needs to see across tenants (the API key lookup, the platform portal) must say so with
/// IgnoreQueryFilters([TenantFilter]), and to the database (layer 3, Row-Level Security) with
/// <see cref="AcrossTenantsAsync"/>.
/// </summary>
public class AppDbContext(
    DbContextOptions<AppDbContext> options,
    ITenantContext tenantContext,
    ICurrentUser currentUser,
    IOperationsFeed? operationsFeed = null)
    : IdentityDbContext<AppUser, AppRole, long>(options), IAppDbContext
{
    public const string TenantFilter = QueryFilters.Tenant;
    public const string MerchantFilter = QueryFilters.Merchant;

    /// <summary>What the courier's dashboards count: a save touching one of these tells them to read again.</summary>
    private static readonly Type[] Operations =
        [typeof(Parcel), typeof(PickupRequest), typeof(DeliveryRun), typeof(DeliveryAttempt), typeof(Rider), typeof(Bag), typeof(CashDeposit)];

    public DbSet<Tenant> Tenants => Set<Tenant>();

    public DbSet<Hub> Hubs => Set<Hub>();

    public DbSet<Zone> Zones => Set<Zone>();

    public DbSet<Area> Areas => Set<Area>();

    public DbSet<DeliveryRate> DeliveryRates => Set<DeliveryRate>();
    public DbSet<DeliveryOption> DeliveryOptions => Set<DeliveryOption>();

    public DbSet<Merchant> Merchants => Set<Merchant>();

    public DbSet<MerchantApiKey> MerchantApiKeys => Set<MerchantApiKey>();

    public DbSet<MerchantPicture> MerchantPictures => Set<MerchantPicture>();

    public DbSet<Moderator> Moderators => Set<Moderator>();

    public DbSet<StaffMember> StaffMembers => Set<StaffMember>();

    public DbSet<MerchantPayoutAccount> MerchantPayoutAccounts => Set<MerchantPayoutAccount>();

    public DbSet<PickupPoint> PickupPoints => Set<PickupPoint>();

    public DbSet<SignUpCode> SignUpCodes => Set<SignUpCode>();

    public DbSet<Parcel> Parcels => Set<Parcel>();

    public DbSet<ParcelEvent> ParcelEvents => Set<ParcelEvent>();

    public DbSet<ParcelRequest> ParcelRequests => Set<ParcelRequest>();

    public DbSet<Rider> Riders => Set<Rider>();

    public DbSet<PickupRequest> PickupRequests => Set<PickupRequest>();

    public DbSet<DeliveryRun> DeliveryRuns => Set<DeliveryRun>();

    public DbSet<DeliveryAttempt> DeliveryAttempts => Set<DeliveryAttempt>();

    public DbSet<VehicleCapacity> VehicleCapacities => Set<VehicleCapacity>();

    public DbSet<ReturnList> ReturnLists => Set<ReturnList>();

    public DbSet<ReturnListParcel> ReturnListParcels => Set<ReturnListParcel>();

    public DbSet<Bag> Bags => Set<Bag>();

    public DbSet<BagParcel> BagParcels => Set<BagParcel>();

    public DbSet<CashDeposit> CashDeposits => Set<CashDeposit>();

    public DbSet<LedgerEntry> LedgerEntries => Set<LedgerEntry>();

    public DbSet<Payout> Payouts => Set<Payout>();

    public DbSet<OnlinePayment> OnlinePayments => Set<OnlinePayment>();

    public DbSet<PayoutRequest> PayoutRequests => Set<PayoutRequest>();

    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();

    public DbSet<MailServer> MailServers => Set<MailServer>();

    public DbSet<GatewayStore> GatewayStores => Set<GatewayStore>();

    /// <summary>Read by the tenant filter at query time. Null means "no tenant", which matches no row.</summary>
    public long? CurrentTenantId => tenantContext.TenantId;

    /// <summary>Read by the merchant filter at query time. Null means the caller is not a merchant.</summary>
    public long? CurrentMerchantId => currentUser.MerchantId;

    /// <summary>
    /// Until the returned scope is disposed, this context's connection sees every tenant's rows in the database
    /// (Row-Level Security, <see cref="TenantSessionInterceptor"/>); the query filter must still be lifted with
    /// IgnoreQueryFilters([TenantFilter]). Only for reads whose point is to cross tenants: the API key lookup and the
    /// platform page. Keep it short, and set no tenant inside it.
    /// </summary>
    public async Task<IAsyncDisposable> AcrossTenantsAsync(CancellationToken cancellationToken = default)
    {
        await Database.OpenConnectionAsync(cancellationToken);
        await Database.ExecuteSqlRawAsync(
            $"EXEC sys.sp_set_session_context @key = N'{TenantSessionInterceptor.AllTenantsKey}', @value = 1;",
            cancellationToken);

        return new AcrossTenants(this);
    }

    private sealed class AcrossTenants(AppDbContext db) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await db.Database.ExecuteSqlRawAsync(
                $"EXEC sys.sp_set_session_context @key = N'{TenantSessionInterceptor.AllTenantsKey}', @value = NULL;");
            await db.Database.CloseConnectionAsync();
        }
    }

    /// <summary>
    /// Saves, and writes every domain event raised by the saved entities to the outbox in the same transaction:
    /// first the changes (so the new rows have their ids), then the outbox rows, then commit. A caller's own
    /// transaction is joined instead. Events are cleared only once committed, so a failed save can be retried.
    /// Once a change to operations is saved, the operator's dashboards are told (<see cref="IOperationsFeed"/>).
    /// </summary>
    public override async Task<int> SaveChangesAsync(
        bool acceptAllChangesOnSuccess,
        CancellationToken cancellationToken = default)
    {
        var tenantId = tenantContext.TenantId;
        var touchesOperations = operationsFeed is not null && tenantId is not null && ChangeTracker.Entries()
            .Any(entry => entry.State is EntityState.Added or EntityState.Modified or EntityState.Deleted &&
                Operations.Contains(entry.Metadata.ClrType));
        var saved = await SaveWithOutboxAsync(acceptAllChangesOnSuccess, cancellationToken);
        if (touchesOperations)
        {
            operationsFeed!.Changed(tenantId!.Value);
        }

        return saved;
    }

    private async Task<int> SaveWithOutboxAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken)
    {
        var raisers = ChangeTracker.Entries<Entity>()
            .Select(entry => entry.Entity)
            .Where(entity => entity.GetDomainEvents().Count > 0)
            .ToList();
        if (raisers.Count == 0)
        {
            return await base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
        }

        var transaction = Database.CurrentTransaction is null
            ? await Database.BeginTransactionAsync(cancellationToken)
            : null;
        try
        {
            var saved = await base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
            OutboxMessages.AddRange(raisers.SelectMany(entity => entity.GetDomainEvents()).SelectMany(OutboxContracts.ToOutbox));
            saved += await base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
            if (transaction is not null)
            {
                await transaction.CommitAsync(cancellationToken);
            }

            raisers.ForEach(entity => entity.ClearDomainEvents());

            return saved;
        }
        finally
        {
            if (transaction is not null)
            {
                await transaction.DisposeAsync();
            }
        }
    }

    /// <summary>Every save goes through <see cref="SaveChangesAsync(bool, CancellationToken)"/>, which writes the outbox.</summary>
    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        if (ChangeTracker.Entries<Entity>().Any(entry => entry.Entity.GetDomainEvents().Count > 0))
        {
            throw new InvalidOperationException("Entities with domain events must be saved with SaveChangesAsync.");
        }

        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        // Every DateTime is stored as UTC; this stamps the Kind back on when it is read
        configurationBuilder.Properties<DateTime>().HaveConversion<UtcDateTimeConverter>();
        configurationBuilder.Properties<string>().AreUnicode();
    }

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);
        builder.ApplyConfigurationsFromAssembly(typeof(AppDbContext).Assembly);
        IdentityMapping.Configure(builder);
        ApplyIsolationFilters(builder);
    }

    private void ApplyIsolationFilters(ModelBuilder builder)
    {
        var context = Expression.Constant(this);
        var tenantId = Expression.Property(context, nameof(CurrentTenantId));
        var merchantId = Expression.Property(context, nameof(CurrentMerchantId));

        foreach (var entityType in builder.Model.GetEntityTypes().Where(t => t.BaseType is null))
        {
            var clrType = entityType.ClrType;
            var row = Expression.Parameter(clrType, "row");

            if (typeof(ITenantOwned).IsAssignableFrom(clrType))
            {
                // row => (long?)row.TenantId == this.CurrentTenantId
                var rowTenant = Expression.Convert(Expression.Property(row, nameof(ITenantOwned.TenantId)), typeof(long?));
                builder.Entity(clrType).HasQueryFilter(
                    TenantFilter,
                    Expression.Lambda(Expression.Equal(rowTenant, tenantId), row));
            }

            var merchantKey = typeof(IMerchantOwned).IsAssignableFrom(clrType)
                ? nameof(IMerchantOwned.MerchantId)
                : clrType == typeof(Merchant) ? nameof(Merchant.Id) : null;
            if (merchantKey is not null)
            {
                // row => this.CurrentMerchantId == null || (long?)row.MerchantId == this.CurrentMerchantId
                var rowMerchant = Expression.Convert(Expression.Property(row, merchantKey), typeof(long?));
                var notAMerchant = Expression.Equal(merchantId, Expression.Constant(null, typeof(long?)));
                builder.Entity(clrType).HasQueryFilter(
                    MerchantFilter,
                    Expression.Lambda(Expression.OrElse(notAMerchant, Expression.Equal(rowMerchant, merchantId)), row));
            }
        }

        // Users are tenant-scoped too, but platform staff have no tenant: null matches null here
        builder.Entity<AppUser>().HasQueryFilter(TenantFilter, user => user.TenantId == CurrentTenantId);
    }
}
