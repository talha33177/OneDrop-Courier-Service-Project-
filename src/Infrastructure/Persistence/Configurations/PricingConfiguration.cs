using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Domain.Pricing;

namespace Infrastructure.Persistence.Configurations;

public class DeliveryRateConfiguration : IEntityTypeConfiguration<DeliveryRate>
{
    public void Configure(EntityTypeBuilder<DeliveryRate> builder)
    {
        builder.MapTenantOwned(Schemas.Pricing);
        builder.Property(r => r.BaseCharge).HasPrecision(10, 2);
        builder.Property(r => r.ExtraKgCharge).HasPrecision(10, 2);
        builder.Property(r => r.CodChargePercent).HasPrecision(5, 2);
        builder.Property(r => r.ReturnCharge).HasPrecision(10, 2);
        builder.HasIndex(r => new { r.TenantId, r.ServiceArea }).IsUnique().HasDatabaseName("UX_DeliveryRate_Tenant_ServiceArea");
    }
}

public class DeliveryOptionConfiguration : IEntityTypeConfiguration<DeliveryOption>
{
    public void Configure(EntityTypeBuilder<DeliveryOption> builder)
    {
        builder.MapTenantOwned(Schemas.Pricing);
        builder.Property(o => o.Code).HasMaxLength(DeliveryOption.MaxCodeLength);
        builder.Property(o => o.Name).HasMaxLength(DeliveryOption.MaxNameLength);
        builder.Property(o => o.Description).HasMaxLength(DeliveryOption.MaxDescriptionLength);
        builder.Property(o => o.Handling).HasMaxLength(DeliveryOption.MaxHandlingLength);
        builder.Property(o => o.Icon).HasMaxLength(DeliveryOption.MaxIconLength);
        builder.Ignore(o => o.KindName);
        builder.HasMany(o => o.Charges)
            .WithOne()
            .HasForeignKey(c => c.DeliveryOptionId)
            .OnDelete(DeleteBehavior.ClientCascade);
        builder.Navigation(o => o.Charges).HasField("charges").UsePropertyAccessMode(PropertyAccessMode.Field);
        builder.HasIndex(o => new { o.TenantId, o.Kind, o.Code }).IsUnique().HasDatabaseName("UX_DeliveryOption_Tenant_Kind_Code");
    }
}

public class DeliveryOptionChargeConfiguration : IEntityTypeConfiguration<DeliveryOptionCharge>
{
    public void Configure(EntityTypeBuilder<DeliveryOptionCharge> builder)
    {
        builder.MapTenantOwned(Schemas.Pricing);
        builder.Property(c => c.Charge).HasPrecision(10, 2);
        builder.HasIndex(c => new { c.TenantId, c.DeliveryOptionId, c.ServiceArea })
            .IsUnique()
            .HasDatabaseName("UX_DeliveryOptionCharge_Tenant_Option_ServiceArea");
    }
}
