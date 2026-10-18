using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Domain.Notifications;

namespace Infrastructure.Persistence.Configurations;

public class OutboxMessageConfiguration : IEntityTypeConfiguration<OutboxMessage>
{
    public void Configure(EntityTypeBuilder<OutboxMessage> builder)
    {
        builder.MapTenantOwned(Schemas.Notifications);
        builder.Property(m => m.Type).HasMaxLength(100);
        builder.Property(m => m.Payload).HasMaxLength(1000);
        builder.Property(m => m.LastError).HasMaxLength(OutboxMessage.MaxErrorLength);
        builder.HasIndex(m => new { m.TenantId, m.Status, m.NextAttemptOn })
            .HasDatabaseName("IX_OutboxMessage_Tenant_Status_NextAttemptOn");
    }
}

public class MailServerConfiguration : IEntityTypeConfiguration<MailServer>
{
    public void Configure(EntityTypeBuilder<MailServer> builder)
    {
        builder.MapTenantOwned(Schemas.Notifications);
        builder.Property(s => s.Host).HasMaxLength(200);
        builder.Property(s => s.UserName).HasMaxLength(254);
        builder.Property(s => s.Password).HasMaxLength(2000);
        builder.Property(s => s.FromAddress).HasMaxLength(254);
        builder.Property(s => s.FromName).HasMaxLength(200);
        builder.Ignore(s => s.HasPassword);
        builder.HasIndex(s => s.TenantId).IsUnique().HasDatabaseName("UX_MailServer_Tenant");
    }
}
