using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Domain.Delivery;
using Domain.Merchants;
using Domain.Network;
using Domain.Notifications;
using Domain.Parcels;
using Domain.Payments;
using Domain.Platform;
using Domain.Pricing;
using Domain.Staff;

namespace Application.Abstractions;

/// <summary>
/// The database as the use cases see it. Every set except <see cref="Tenants"/> is already filtered to the
/// current tenant, and the merchant-owned sets to the current merchant when the caller is one.
/// </summary>
public interface IAppDbContext
{
    DbSet<Tenant> Tenants { get; }

    DbSet<Hub> Hubs { get; }

    DbSet<Zone> Zones { get; }

    DbSet<Area> Areas { get; }

    DbSet<DeliveryRate> DeliveryRates { get; }

    DbSet<DeliveryOption> DeliveryOptions { get; }

    DbSet<Merchant> Merchants { get; }

    DbSet<MerchantApiKey> MerchantApiKeys { get; }

    DbSet<MerchantPicture> MerchantPictures { get; }

    DbSet<Moderator> Moderators { get; }

    DbSet<StaffMember> StaffMembers { get; }

    DbSet<MerchantPayoutAccount> MerchantPayoutAccounts { get; }

    DbSet<PickupPoint> PickupPoints { get; }

    DbSet<SignUpCode> SignUpCodes { get; }

    DbSet<Parcel> Parcels { get; }

    DbSet<ParcelEvent> ParcelEvents { get; }

    DbSet<ParcelRequest> ParcelRequests { get; }

    DbSet<Rider> Riders { get; }

    DbSet<PickupRequest> PickupRequests { get; }

    DbSet<DeliveryRun> DeliveryRuns { get; }

    DbSet<DeliveryAttempt> DeliveryAttempts { get; }

    DbSet<VehicleCapacity> VehicleCapacities { get; }

    DbSet<ReturnList> ReturnLists { get; }

    DbSet<ReturnListParcel> ReturnListParcels { get; }

    DbSet<LedgerEntry> LedgerEntries { get; }

    DbSet<Payout> Payouts { get; }

    DbSet<OnlinePayment> OnlinePayments { get; }

    DbSet<PayoutRequest> PayoutRequests { get; }

    DbSet<OutboxMessage> OutboxMessages { get; }

    DbSet<MailServer> MailServers { get; }

    DbSet<GatewayStore> GatewayStores { get; }

    /// <summary>For the rare change that needs two saves in one transaction.</summary>
    DatabaseFacade Database { get; }

    EntityEntry<TEntity> Entry<TEntity>(TEntity entity)
        where TEntity : class;

    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}
