using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Domain.Merchants;
using Domain.Network;

namespace Infrastructure.Persistence.Configurations;

public class MerchantConfiguration : IEntityTypeConfiguration<Merchant>
{
    public void Configure(EntityTypeBuilder<Merchant> builder)
    {
        builder.MapTenantOwned(Schemas.Merchants);
        builder.Property(m => m.Name).HasMaxLength(200);
        builder.Property(m => m.OwnerName).HasMaxLength(200);
        builder.Property(m => m.ContactPhone).HasMaxLength(20);
        builder.Property(m => m.ContactEmail).HasMaxLength(320);
        builder.Property(m => m.Address).HasMaxLength(500);
        builder.Property(m => m.PayoutAccount).HasMaxLength(30);
        builder.Property(m => m.PayoutAccountName).HasMaxLength(200);
        builder.Property(m => m.WebhookUrl).HasMaxLength(Merchant.MaxWebhookUrlLength);
        builder.Property(m => m.WebhookSecret).HasMaxLength(100);
        builder.Property(m => m.PayoutHold).HasMaxLength(Merchant.MaxPayoutHoldLength);
        builder.Ignore(m => m.CanBook);
        builder.Ignore(m => m.HasPayoutAccount);
        builder.Ignore(m => m.ArePayoutsHeld);
        builder.Ignore(m => m.IsMainProfile);
        builder.Ignore(m => m.AccountId);
        builder.HasOne<Merchant>().WithMany().HasForeignKey(m => m.MainMerchantId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(m => new { m.TenantId, m.Name }).IsUnique().HasDatabaseName("UX_Merchant_Tenant_Name");
        builder.HasIndex(m => new { m.TenantId, m.MainMerchantId }).HasDatabaseName("IX_Merchant_Tenant_MainMerchant");
    }
}

public class MerchantApiKeyConfiguration : IEntityTypeConfiguration<MerchantApiKey>
{
    public void Configure(EntityTypeBuilder<MerchantApiKey> builder)
    {
        builder.MapTenantOwned(Schemas.Merchants);
        builder.Property(k => k.Name).HasMaxLength(100);
        builder.Property(k => k.Prefix).HasMaxLength(MerchantApiKey.PrefixLength);
        builder.Property(k => k.KeyHash).HasMaxLength(32).IsFixedLength();
        builder.Property(k => k.LastUsedOn).HasColumnType("datetime2(0)");
        builder.Property(k => k.RevokedOn).HasColumnType("datetime2(0)");
        builder.Ignore(k => k.IsActive);
        builder.HasOne<Merchant>().WithMany().HasForeignKey(k => k.MerchantId).OnDelete(DeleteBehavior.Restrict);

        // Global, not per tenant: the prefix is how a key is found before the tenant is known
        builder.HasIndex(k => k.Prefix).IsUnique().HasDatabaseName("UX_MerchantApiKey_Prefix");
    }
}

public class MerchantPictureConfiguration : IEntityTypeConfiguration<MerchantPicture>
{
    public void Configure(EntityTypeBuilder<MerchantPicture> builder)
    {
        builder.MapTenantOwned(Schemas.Merchants);
        builder.Property(p => p.ContentType).HasMaxLength(50);
        builder.Property(p => p.Content).HasColumnType("varbinary(max)");
        builder.HasOne<Merchant>().WithMany().HasForeignKey(p => p.MerchantId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(p => new { p.TenantId, p.MerchantId }).IsUnique().HasDatabaseName("UX_MerchantPicture_Tenant_Merchant");
    }
}

public class ModeratorConfiguration : IEntityTypeConfiguration<Moderator>
{
    public void Configure(EntityTypeBuilder<Moderator> builder)
    {
        builder.MapTenantOwned(Schemas.Merchants);
        builder.Property(m => m.Name).HasMaxLength(Moderator.MaxNameLength);
        builder.Property(m => m.Phone).HasMaxLength(20);
        builder.Property(m => m.Permissions).HasConversion<int>();
        builder.HasOne<Merchant>().WithMany().HasForeignKey(m => m.AccountId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(m => new { m.TenantId, m.UserId }).IsUnique().HasDatabaseName("UX_Moderator_Tenant_User");
        builder.HasIndex(m => new { m.TenantId, m.AccountId }).HasDatabaseName("IX_Moderator_Tenant_Account");
    }
}

public class MerchantPayoutAccountConfiguration : IEntityTypeConfiguration<MerchantPayoutAccount>
{
    public void Configure(EntityTypeBuilder<MerchantPayoutAccount> builder)
    {
        builder.MapTenantOwned(Schemas.Merchants);
        builder.Property(a => a.Number).HasMaxLength(30);
        builder.Property(a => a.Name).HasMaxLength(200);
        builder.HasOne<Merchant>().WithMany().HasForeignKey(a => a.AccountId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(a => new { a.TenantId, a.AccountId, a.Method, a.Number })
            .IsUnique()
            .HasFilter("[Archived] = (0)")
            .HasDatabaseName("UX_MerchantPayoutAccount_Account_Number");
    }
}

public class PickupPointConfiguration: IEntityTypeConfiguration<PickupPoint>
{
    public void Configure(EntityTypeBuilder<PickupPoint> builder)
    {
        builder.MapTenantOwned(Schemas.Merchants);
        builder.Property(p => p.Name).HasMaxLength(200);
        builder.Property(p => p.Address).HasMaxLength(500);
        builder.Property(p => p.ContactPhone).HasMaxLength(20);
        builder.Property(p => p.Latitude).HasPrecision(9, 6);
        builder.Property(p => p.Longitude).HasPrecision(9, 6);
        builder.Ignore(p => p.Pin);
        builder.HasOne<Merchant>().WithMany().HasForeignKey(p => p.MerchantId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Area>().WithMany().HasForeignKey(p => p.AreaId).OnDelete(DeleteBehavior.Restrict);
    }
}
