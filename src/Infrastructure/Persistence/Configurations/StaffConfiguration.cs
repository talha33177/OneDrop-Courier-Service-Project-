using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Domain.Network;
using Domain.Staff;
using Infrastructure.Identity;

namespace Infrastructure.Persistence.Configurations;

public class StaffMemberConfiguration : IEntityTypeConfiguration<StaffMember>
{
    public void Configure(EntityTypeBuilder<StaffMember> builder)
    {
        builder.MapTenantOwned(Schemas.Identity);
        builder.Property(s => s.Permissions).HasConversion<int>();
        builder.HasOne<AppUser>().WithMany().HasForeignKey(s => s.UserId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Hub>().WithMany().HasForeignKey(s => s.HubId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(s => new { s.TenantId, s.UserId }).IsUnique().HasDatabaseName("UX_StaffMember_Tenant_User");
        builder.HasIndex(s => new { s.TenantId, s.HubId }).HasDatabaseName("IX_StaffMember_Tenant_Hub");
    }
}
