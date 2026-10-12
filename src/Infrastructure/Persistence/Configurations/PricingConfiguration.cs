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
        builder.Property(r => r.ExpressCharge).HasPrecision(10, 2);
        builder.Property(r => r.LiquidCharge).HasPrecision(10, 2);
        builder.Property(r => r.FragileCharge).HasPrecision(10, 2);
        builder.Property(r => r.FreshFoodCharge).HasPrecision(10, 2);
        builder.Property(r => r.FrozenCharge).HasPrecision(10, 2);
        builder.HasIndex(r => new { r.TenantId, r.ServiceArea }).IsUnique().HasDatabaseName("UX_DeliveryRate_Tenant_ServiceArea");
    }
}
