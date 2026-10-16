using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Domain.Platform;

namespace Infrastructure.Persistence.Configurations;

public class TenantConfiguration : IEntityTypeConfiguration<Tenant>
{
    public void Configure(EntityTypeBuilder<Tenant> builder)
    {
        builder.MapAudited(Schemas.Platform);
        builder.Property(t => t.Name).HasMaxLength(200);
        builder.Property(t => t.Slug).HasMaxLength(50);
        builder.Property(t => t.TimeZone).HasMaxLength(100);
        builder.Property(t => t.CurrencyCode).HasMaxLength(3).IsFixedLength();
        builder.Property(t => t.SmsSenderName).HasMaxLength(20);
        builder.Property(t => t.SupportPhone).HasMaxLength(20);
        builder.Property(t => t.RiderReturnTime).HasColumnType("time(0)");
        builder.Property(t => t.PickupTime).HasColumnType("time(0)");
        builder.HasIndex(t => t.Slug).IsUnique().HasDatabaseName("UX_Tenant_Slug");
    }
}
