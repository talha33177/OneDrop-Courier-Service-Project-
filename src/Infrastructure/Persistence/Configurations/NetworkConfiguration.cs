using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Domain.Network;

namespace Infrastructure.Persistence.Configurations;

public class HubConfiguration : IEntityTypeConfiguration<Hub>
{
    public void Configure(EntityTypeBuilder<Hub> builder)
    {
        builder.MapTenantOwned(Schemas.Network);
        builder.Property(h => h.Code).HasMaxLength(20);
        builder.Property(h => h.Name).HasMaxLength(200);
        builder.Property(h => h.Address).HasMaxLength(500);
        builder.Property(h => h.Phone).HasMaxLength(20);
        builder.HasIndex(h => new { h.TenantId, h.Code }).IsUnique().HasDatabaseName("UX_Hub_Tenant_Code");
    }
}

public class ZoneConfiguration : IEntityTypeConfiguration<Zone>
{
    public void Configure(EntityTypeBuilder<Zone> builder)
    {
        builder.MapTenantOwned(Schemas.Network);
        builder.Property(z => z.Code).HasMaxLength(20);
        builder.Property(z => z.Name).HasMaxLength(200);
        builder.Property(z => z.City).HasMaxLength(60);
        builder.Property(z => z.Division).HasMaxLength(30);
        builder.HasOne(z => z.Hub).WithMany().HasForeignKey(z => z.HubId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(z => new { z.TenantId, z.Code }).IsUnique().HasDatabaseName("UX_Zone_Tenant_Code");
    }
}

public class AreaConfiguration : IEntityTypeConfiguration<Area>
{
    public void Configure(EntityTypeBuilder<Area> builder)
    {
        builder.MapTenantOwned(Schemas.Network);
        builder.Property(a => a.Name).HasMaxLength(200);
        builder.HasOne(a => a.Zone).WithMany().HasForeignKey(a => a.ZoneId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(a => new { a.TenantId, a.Name }).IsUnique().HasDatabaseName("UX_Area_Tenant_Name");
    }
}
