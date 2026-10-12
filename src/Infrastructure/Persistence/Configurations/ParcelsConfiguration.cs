using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Domain.Delivery;
using Domain.Merchants;
using Domain.Network;
using Domain.Parcels;
using Infrastructure.Identity;

namespace Infrastructure.Persistence.Configurations;

public class ParcelConfiguration : IEntityTypeConfiguration<Parcel>
{
    public void Configure(EntityTypeBuilder<Parcel> builder)
    {
        builder.MapTenantOwned(Schemas.Parcels);

        // OD10000001, from the Parcels.TrackingNumber sequence in the column default; EF reads it back after insert
        builder.Property(p => p.TrackingCode)
            .HasMaxLength(20)
            .ValueGeneratedOnAdd()
            .HasDefaultValueSql("(concat(N'OD',NEXT VALUE FOR [Parcels].[TrackingNumber]))");
        builder.Property(p => p.MerchantReference).HasMaxLength(100);
        builder.Property(p => p.IdempotencyKey).HasMaxLength(100);
        builder.Property(p => p.RequestHash).HasMaxLength(32).IsFixedLength();
        builder.Property(p => p.RecipientName).HasMaxLength(200);
        builder.Property(p => p.RecipientPhone).HasMaxLength(20);
        builder.Property(p => p.RecipientAddress).HasMaxLength(500);
        builder.Property(p => p.ItemDescription).HasMaxLength(200);
        builder.Property(p => p.Note).HasMaxLength(500);
        builder.Property(p => p.CodAmount).IsMoney();
        builder.Property(p => p.DeliveryCharge).HasPrecision(10, 2);
        builder.Property(p => p.BaseCharge).HasPrecision(10, 2);
        builder.Property(p => p.ExtraKgCharge).HasPrecision(10, 2);
        builder.Property(p => p.CodChargePercent).HasPrecision(5, 2);
        builder.Property(p => p.ReturnCharge).HasPrecision(10, 2);
        builder.Property(p => p.SpeedCharge).HasPrecision(10, 2);
        builder.Property(p => p.ProductCharge).HasPrecision(10, 2);
        builder.Property(p => p.RecipientLatitude).HasPrecision(9, 6);
        builder.Property(p => p.RecipientLongitude).HasPrecision(9, 6);
        builder.Ignore(p => p.Pin);
        builder.Ignore(p => p.IsPriority);
        builder.Property(p => p.HoldReason).HasMaxLength(200);
        builder.Property(p => p.ReturnReason).HasMaxLength(200);
        builder.Property(p => p.IssueNote).HasMaxLength(200);
        builder.Property(p => p.CollectedAmount).HasPrecision(12, 2);
        builder.Property(p => p.CodCharge).HasPrecision(10, 2);
        builder.Property(p => p.RowVersion).IsRowVersion();
        builder.Ignore(p => p.Charges);
        builder.Ignore(p => p.BilledWeightGrams);
        builder.Ignore(p => p.IsFinal);
        builder.Ignore(p => p.TotalCharge);

        builder.HasOne<Merchant>().WithMany().HasForeignKey(p => p.MerchantId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<PickupPoint>().WithMany().HasForeignKey(p => p.PickupPointId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Area>().WithMany().HasForeignKey(p => p.AreaId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Hub>().WithMany().HasForeignKey(p => p.PickupHubId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Hub>().WithMany().HasForeignKey(p => p.DeliveryHubId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Hub>().WithMany().HasForeignKey(p => p.CurrentHubId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Hub>().WithMany().HasForeignKey(p => p.TransferToHubId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Rider>().WithMany().HasForeignKey(p => p.RiderId).OnDelete(DeleteBehavior.Restrict);

        builder.HasMany(p => p.Events).WithOne(e => e.Parcel).HasForeignKey(e => e.ParcelId).OnDelete(DeleteBehavior.Restrict);
        builder.Navigation(p => p.Events).HasField("events");

        builder.HasIndex(p => p.TrackingCode).IsUnique().HasDatabaseName("UX_Parcel_TrackingCode");
        builder.HasIndex(p => new { p.MerchantId, p.IdempotencyKey })
            .IsUnique()
            .HasFilter("[IdempotencyKey] IS NOT NULL")
            .HasDatabaseName("UX_Parcel_Merchant_IdempotencyKey");
    }
}

public class ParcelRequestConfiguration : IEntityTypeConfiguration<ParcelRequest>
{
    public void Configure(EntityTypeBuilder<ParcelRequest> builder)
    {
        builder.MapTenantOwned(Schemas.Parcels);
        builder.Property(r => r.CodAmount).IsMoney();
        builder.Property(r => r.NewCodAmount).HasPrecision(12, 2);
        builder.Property(r => r.Reason).HasMaxLength(200);
        builder.Property(r => r.Answer).HasMaxLength(200);
        builder.Property(r => r.RowVersion).IsRowVersion();
        builder.HasOne<Merchant>().WithMany().HasForeignKey(r => r.MerchantId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Parcel>().WithMany().HasForeignKey(r => r.ParcelId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<AppUser>().WithMany().HasForeignKey(r => r.RequestedById).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<AppUser>().WithMany().HasForeignKey(r => r.AnsweredById).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(r => r.ParcelId)
            .IsUnique()
            .HasFilter("[Status] = 1")
            .HasDatabaseName("UX_ParcelRequest_Parcel_Open");
    }
}

public class ParcelEventConfiguration : IEntityTypeConfiguration<ParcelEvent>
{
    public void Configure(EntityTypeBuilder<ParcelEvent> builder)
    {
        builder.MapTenantOwned(Schemas.Parcels);
        builder.Property(e => e.Note).HasMaxLength(300);
        builder.HasOne<Merchant>().WithMany().HasForeignKey(e => e.MerchantId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Hub>().WithMany().HasForeignKey(e => e.HubId).OnDelete(DeleteBehavior.Restrict);
    }
}
