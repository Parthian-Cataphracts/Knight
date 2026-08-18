using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Knight.Infrastructure.Auditing;

namespace Knight.Infrastructure.Persistence.Configurations;

internal sealed class AuditLogEntryConfiguration : IEntityTypeConfiguration<AuditLogEntry>
{
    public void Configure(EntityTypeBuilder<AuditLogEntry> builder)
    {
        builder.ToTable("audit_log_entries");

        builder.HasKey(e => e.Id);

        builder.Property(e => e.ActorType).HasMaxLength(30).IsRequired();
        builder.Property(e => e.Action).HasMaxLength(100).IsRequired();
        builder.Property(e => e.EntityType).HasMaxLength(100);
        builder.Property(e => e.EntityId).HasMaxLength(200);
        builder.Property(e => e.MetadataJson).HasColumnType("jsonb");

        builder.HasIndex(e => e.TenantId);
        builder.HasIndex(e => e.OccurredAt);
        builder.HasIndex(e => e.Action);
    }
}
