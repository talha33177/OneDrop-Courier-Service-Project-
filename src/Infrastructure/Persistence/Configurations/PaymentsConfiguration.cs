using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Domain.Merchants;
using Domain.Parcels;
using Domain.Payments;

namespace Infrastructure.Persistence.Configurations;

public class LedgerEntryConfiguration : IEntityTypeConfiguration<LedgerEntry>
{
    public void Configure(EntityTypeBuilder<LedgerEntry> builder)
    {
        builder.MapTenantOwned(Schemas.Payments);
        builder.Property(e => e.Amount).IsMoney();
        builder.Property(e => e.RowVersion).IsRowVersion();
        builder.Ignore(e => e.IsPaidOut);
        builder.HasOne<Merchant>().WithMany().HasForeignKey(e => e.MerchantId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Parcel>().WithMany().HasForeignKey(e => e.ParcelId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(e => e.Payout).WithMany().HasForeignKey(e => e.PayoutId).OnDelete(DeleteBehavior.Restrict);
        builder.Property(e => e.Note).HasMaxLength(LedgerEntry.MaxNoteLength);
        builder.HasIndex(e => new { e.ParcelId, e.Kind })
            .IsUnique()
            .HasFilter("[ParcelId] IS NOT NULL")
            .HasDatabaseName("UX_LedgerEntry_Parcel_Kind");
    }
}

public class PayoutConfiguration : IEntityTypeConfiguration<Payout>
{
    public void Configure(EntityTypeBuilder<Payout> builder)
    {
        builder.MapTenantOwned(Schemas.Payments);

        // INV-100001, from the Payments.PayoutNumber sequence in the column default; EF reads it back after insert
        builder.Property(p => p.Number)
            .HasMaxLength(20)
            .ValueGeneratedOnAdd()
            .HasDefaultValueSql("(concat(N'INV-',NEXT VALUE FOR [Payments].[PayoutNumber]))");
        builder.Property(p => p.CodTotal).IsMoney();
        builder.Property(p => p.ChargesTotal).IsMoney();
        builder.Property(p => p.AdjustmentsTotal).IsMoney();
        builder.Property(p => p.Amount).IsMoney();
        builder.Property(p => p.Account).HasMaxLength(30);
        builder.Property(p => p.GatewayReference).HasMaxLength(100);
        builder.Property(p => p.LastError).HasMaxLength(Payout.MaxErrorLength);
        builder.Ignore(p => p.IsStuck);
        builder.Property(p => p.RowVersion).IsRowVersion();
        builder.HasOne<Merchant>().WithMany().HasForeignKey(p => p.MerchantId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(p => p.Number).IsUnique().HasDatabaseName("UX_Payout_Number");
    }
}

public class PayoutRequestConfiguration : IEntityTypeConfiguration<PayoutRequest>
{
    public void Configure(EntityTypeBuilder<PayoutRequest> builder)
    {
        builder.MapTenantOwned(Schemas.Payments);
        builder.Property(r => r.AmountAsked).IsMoney();
        builder.Property(r => r.Answer).HasMaxLength(PayoutRequest.MaxAnswerLength);
        builder.Ignore(r => r.IsOpen);
        builder.Property(r => r.RowVersion).IsRowVersion();
        builder.HasOne<Merchant>().WithMany().HasForeignKey(r => r.MerchantId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Payout>().WithMany().HasForeignKey(r => r.PayoutId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(r => r.MerchantId)
            .IsUnique()
            .HasFilter("[Status] = 1")
            .HasDatabaseName("UX_PayoutRequest_Merchant_Open");
    }
}

public class GatewayStoreConfiguration : IEntityTypeConfiguration<GatewayStore>
{
    public void Configure(EntityTypeBuilder<GatewayStore> builder)
    {
        builder.MapTenantOwned(Schemas.Payments);
        builder.Property(s => s.SandboxStoreId).HasMaxLength(100);
        builder.Property(s => s.SandboxPassword).HasMaxLength(2000);
        builder.Property(s => s.LiveStoreId).HasMaxLength(100);
        builder.Property(s => s.LivePassword).HasMaxLength(2000);
        builder.Ignore(s => s.HasSandbox);
        builder.Ignore(s => s.HasLive);
        builder.Ignore(s => s.InUse);
        builder.HasIndex(s => s.TenantId).IsUnique().HasDatabaseName("UX_GatewayStore_Tenant");
    }
}

public class OnlinePaymentConfiguration : IEntityTypeConfiguration<OnlinePayment>
{
    public void Configure(EntityTypeBuilder<OnlinePayment> builder)
    {
        builder.MapTenantOwned(Schemas.Payments);

        // PAY-100001, from the Payments.OnlinePaymentNumber sequence in the column default; EF reads it back after insert
        builder.Property(p => p.Number)
            .HasMaxLength(20)
            .ValueGeneratedOnAdd()
            .HasDefaultValueSql("(concat(N'PAY-',NEXT VALUE FOR [Payments].[OnlinePaymentNumber]))");
        builder.Property(p => p.TransactionId).HasMaxLength(OnlinePayment.TransactionIdLength);
        builder.Property(p => p.Amount).IsMoney();
        builder.Property(p => p.Currency).HasMaxLength(3);
        builder.Property(p => p.PaidAmount).HasPrecision(12, 2);
        builder.Property(p => p.StoreAmount).HasPrecision(12, 2);
        builder.Property(p => p.Method).HasMaxLength(50);
        builder.Property(p => p.ValidationId).HasMaxLength(100);
        builder.Property(p => p.BankTransactionId).HasMaxLength(80);
        builder.Property(p => p.Note).HasMaxLength(OnlinePayment.MaxNoteLength);
        builder.Property(p => p.PayerEmail).HasMaxLength(254);
        builder.Property(p => p.RowVersion).IsRowVersion();
        builder.HasOne<Merchant>().WithMany().HasForeignKey(p => p.MerchantId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(p => p.LedgerEntry).WithMany().HasForeignKey(p => p.LedgerEntryId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Parcel>().WithMany().HasForeignKey(p => p.ParcelId).OnDelete(DeleteBehavior.Restrict);
        builder.Property(p => p.RefundReference).HasMaxLength(80);
        builder.HasIndex(p => p.ParcelId)
            .IsUnique()
            .HasFilter("[Status] = 2 AND [ParcelId] IS NOT NULL")
            .HasDatabaseName("UX_OnlinePayment_Parcel_Paid");
        builder.HasIndex(p => p.Number).IsUnique().HasDatabaseName("UX_OnlinePayment_Number");
        builder.HasIndex(p => p.TransactionId).IsUnique().HasDatabaseName("UX_OnlinePayment_TransactionId");
        builder.HasIndex(p => p.LedgerEntryId)
            .IsUnique()
            .HasFilter("[LedgerEntryId] IS NOT NULL")
            .HasDatabaseName("UX_OnlinePayment_LedgerEntry");
    }
}
