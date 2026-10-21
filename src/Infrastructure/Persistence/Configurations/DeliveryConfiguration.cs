using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Domain.Delivery;
using Domain.Merchants;
using Domain.Network;
using Domain.Parcels;
using Infrastructure.Identity;

namespace Infrastructure.Persistence.Configurations;

public class RiderConfiguration : IEntityTypeConfiguration<Rider>
{
    public void Configure(EntityTypeBuilder<Rider> builder)
    {
        builder.MapTenantOwned(Schemas.Delivery);
        builder.Property(r => r.Name).HasMaxLength(200);
        builder.Property(r => r.Phone).HasMaxLength(20);
        builder.HasOne<Hub>().WithMany().HasForeignKey(r => r.HubId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<AppUser>().WithMany().HasForeignKey(r => r.UserId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(r => r.UserId)
            .IsUnique()
            .HasFilter("[UserId] IS NOT NULL")
            .HasDatabaseName("UX_Rider_User");
    }
}

public class VehicleCapacityConfiguration : IEntityTypeConfiguration<VehicleCapacity>
{
    public void Configure(EntityTypeBuilder<VehicleCapacity> builder)
    {
        builder.MapTenantOwned(Schemas.Delivery);
        builder.HasIndex(c => new { c.TenantId, c.Vehicle }).IsUnique().HasDatabaseName("UX_VehicleCapacity_Tenant_Vehicle");
    }
}

public class DeliveryRunConfiguration : IEntityTypeConfiguration<DeliveryRun>
{
    public void Configure(EntityTypeBuilder<DeliveryRun> builder)
    {
        builder.MapTenantOwned(Schemas.Delivery);
        builder.Property(r => r.CashExpected).HasPrecision(12, 2);
        builder.Property(r => r.CashReceived).HasPrecision(12, 2);
        builder.Ignore(r => r.CashShort);
        builder.Ignore(r => r.ShortWaiting);
        builder.Property(r => r.ShortNote).HasMaxLength(DeliveryRun.MaxShortNoteLength);
        builder.Property(r => r.RowVersion).IsRowVersion();
        builder.HasOne<Rider>().WithMany().HasForeignKey(r => r.RiderId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Hub>().WithMany().HasForeignKey(r => r.HubId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<AppUser>().WithMany().HasForeignKey(r => r.ShortAcceptedById).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(r => new { r.RiderId, r.RunDate }).IsUnique().HasDatabaseName("UX_DeliveryRun_Rider_RunDate");
    }
}

public class DeliveryAttemptConfiguration : IEntityTypeConfiguration<DeliveryAttempt>
{
    public void Configure(EntityTypeBuilder<DeliveryAttempt> builder)
    {
        builder.MapTenantOwned(Schemas.Delivery);
        builder.Property(a => a.CollectedAmount).IsMoney();
        builder.Property(a => a.Reason).HasMaxLength(200);
        builder.Ignore(a => a.IsDone);
        builder.HasOne<DeliveryRun>().WithMany().HasForeignKey(a => a.RunId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Rider>().WithMany().HasForeignKey(a => a.RiderId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Parcel>().WithMany().HasForeignKey(a => a.ParcelId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Merchant>().WithMany().HasForeignKey(a => a.MerchantId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(a => a.ParcelId)
            .IsUnique()
            .HasFilter("[Outcome] IS NULL")
            .HasDatabaseName("UX_DeliveryAttempt_Parcel_Open");
    }
}

public class ReturnListConfiguration : IEntityTypeConfiguration<ReturnList>
{
    public void Configure(EntityTypeBuilder<ReturnList> builder)
    {
        builder.MapTenantOwned(Schemas.Delivery);

        // RL-100001, from the Delivery.ReturnListNumber sequence in the column default; EF reads it back after insert
        builder.Property(l => l.Number)
            .HasMaxLength(20)
            .ValueGeneratedOnAdd()
            .HasDefaultValueSql("(concat(N'RL-',NEXT VALUE FOR [Delivery].[ReturnListNumber]))");
        builder.Property(l => l.Note).HasMaxLength(ReturnList.MaxNoteLength);
        builder.Property(l => l.RowVersion).IsRowVersion();
        builder.HasOne<Merchant>().WithMany().HasForeignKey(l => l.MerchantId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<PickupPoint>().WithMany().HasForeignKey(l => l.PickupPointId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Hub>().WithMany().HasForeignKey(l => l.HubId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Rider>().WithMany().HasForeignKey(l => l.RiderId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<AppUser>().WithMany().HasForeignKey(l => l.ConfirmedById).OnDelete(DeleteBehavior.Restrict);
        builder.HasMany(l => l.Parcels).WithOne(p => p.ReturnList).HasForeignKey(p => p.ReturnListId).OnDelete(DeleteBehavior.Restrict);
        builder.Navigation(l => l.Parcels).HasField("parcels");
        builder.HasIndex(l => l.Number).IsUnique().HasDatabaseName("UX_ReturnList_Number");
    }
}

public class ReturnListParcelConfiguration : IEntityTypeConfiguration<ReturnListParcel>
{
    public void Configure(EntityTypeBuilder<ReturnListParcel> builder)
    {
        builder.MapTenantOwned(Schemas.Delivery);
        builder.HasOne<Parcel>().WithMany().HasForeignKey(p => p.ParcelId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Merchant>().WithMany().HasForeignKey(p => p.MerchantId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(p => new { p.ReturnListId, p.ParcelId }).IsUnique().HasDatabaseName("UX_ReturnListParcel_ReturnList_Parcel");
    }
}

public class BagConfiguration : IEntityTypeConfiguration<Bag>
{
    public void Configure(EntityTypeBuilder<Bag> builder)
    {
        builder.MapTenantOwned(Schemas.Delivery);

        // BG-100001, from the Delivery.BagNumber sequence in the column default; EF reads it back after insert
        builder.Property(b => b.Number)
            .HasMaxLength(20)
            .ValueGeneratedOnAdd()
            .HasDefaultValueSql("(concat(N'BG-',NEXT VALUE FOR [Delivery].[BagNumber]))");
        builder.Property(b => b.Vehicle).HasMaxLength(Bag.MaxVehicleLength);
        builder.Property(b => b.RowVersion).IsRowVersion();
        builder.HasOne<Hub>().WithMany().HasForeignKey(b => b.FromHubId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Hub>().WithMany().HasForeignKey(b => b.ToHubId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<AppUser>().WithMany().HasForeignKey(b => b.SealedById).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<AppUser>().WithMany().HasForeignKey(b => b.ClosedById).OnDelete(DeleteBehavior.Restrict);
        // A parcel taken out of an open bag is its line deleted; the table's key itself never cascades
        builder.HasMany(b => b.Parcels).WithOne(p => p.Bag).HasForeignKey(p => p.BagId).OnDelete(DeleteBehavior.ClientCascade);
        builder.Navigation(b => b.Parcels).HasField("parcels");
        builder.HasIndex(b => b.Number).IsUnique().HasDatabaseName("UX_Bag_Number");
        builder.HasIndex(b => new { b.TenantId, b.FromHubId, b.ToHubId })
            .IsUnique()
            .HasFilter("[Status] = 1")
            .HasDatabaseName("UX_Bag_From_To_Open");
    }
}

public class BagParcelConfiguration : IEntityTypeConfiguration<BagParcel>
{
    public void Configure(EntityTypeBuilder<BagParcel> builder)
    {
        builder.MapTenantOwned(Schemas.Delivery);
        builder.HasOne<Parcel>().WithMany().HasForeignKey(p => p.ParcelId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(p => new { p.BagId, p.ParcelId }).IsUnique().HasDatabaseName("UX_BagParcel_Bag_Parcel");
    }
}

public class PickupRequestConfiguration : IEntityTypeConfiguration<PickupRequest>
{
    public void Configure(EntityTypeBuilder<PickupRequest> builder)
    {
        builder.MapTenantOwned(Schemas.Delivery);
        builder.Property(r => r.RowVersion).IsRowVersion();
        builder.Ignore(r => r.IsOpen);
        builder.HasOne<Merchant>().WithMany().HasForeignKey(r => r.MerchantId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<PickupPoint>().WithMany().HasForeignKey(r => r.PickupPointId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Hub>().WithMany().HasForeignKey(r => r.HubId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Rider>().WithMany().HasForeignKey(r => r.RiderId).OnDelete(DeleteBehavior.Restrict);
    }
}
