using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Servers.Domain;

namespace Knight.Infrastructure.ControlPlane.Configurations;

/// <summary>
/// Mapping for infrastructure and monitoring: servers, agents, metric samples and
/// alerts.
///
/// None of these is customer-scoped, and that is deliberate rather than an
/// oversight. A server is platform infrastructure; a customer sees the health of
/// their store, never the machine it shares with somebody else's
/// (docs/authorization.md). The one exception is <c>Alert</c>, which carries an
/// optional customer id so that a store-sourced alert can be shown to the
/// customer it concerns while a shared-server alert stays platform-only.
/// </summary>
internal sealed class ServerConfiguration : IEntityTypeConfiguration<Server>
{
    public void Configure(EntityTypeBuilder<Server> builder)
    {
        builder.ToTable("servers");

        builder.HasKey(s => s.Id);

        builder.Property(s => s.Name).HasMaxLength(200).IsRequired();
        builder.Property(s => s.HostingModel).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(s => s.Environment).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(s => s.Status).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(s => s.Provider).HasMaxLength(100);
        builder.Property(s => s.Region).HasMaxLength(100);

        // Long enough for an IPv6 address with a zone index.
        builder.Property(s => s.IpAddress).HasMaxLength(45);
        builder.Property(s => s.StatusReason).HasMaxLength(500);

        builder.Ignore(s => s.IsActive);

        builder.HasIndex(s => new { s.Environment, s.Status });

        // The sweep asks for exactly this: active servers that have reported.
        builder.HasIndex(s => s.LastSeenAt);
    }
}

internal sealed class AgentConfiguration : IEntityTypeConfiguration<Agent>
{
    public void Configure(EntityTypeBuilder<Agent> builder)
    {
        builder.ToTable("agents");

        builder.HasKey(a => a.Id);

        builder.Property(a => a.Version).HasMaxLength(50);
        builder.Property(a => a.Status).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(a => a.RevokedReason).HasMaxLength(500);
        builder.Property(a => a.Capabilities).HasColumnType("jsonb");

        // Both are hashes, never the secrets themselves. A leak of this table
        // does not let anyone impersonate an agent (risks.md R22).
        builder.Property(a => a.ProvisioningTokenHash).HasMaxLength(200);
        builder.Property(a => a.CredentialHash).HasMaxLength(200);

        builder.Ignore(a => a.IsUsable);

        builder.HasIndex(a => a.ServerId);

        // Enrolment scans agents awaiting a token, and the offline sweep scans
        // enrolled ones. Both are status reads.
        builder.HasIndex(a => a.Status);

        builder.HasOne<Server>()
            .WithMany()
            .HasForeignKey(a => a.ServerId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class ServerMetricConfiguration : IEntityTypeConfiguration<ServerMetric>
{
    public void Configure(EntityTypeBuilder<ServerMetric> builder)
    {
        builder.ToTable("server_metrics");

        builder.HasKey(m => m.Id);

        builder.Property(m => m.CpuPercent).IsRequired();
        builder.Property(m => m.MemoryUsedBytes).IsRequired();
        builder.Property(m => m.MemoryTotalBytes).IsRequired();
        builder.Property(m => m.DiskUsedBytes).IsRequired();
        builder.Property(m => m.DiskTotalBytes).IsRequired();

        builder.Ignore(m => m.MemoryPercent);
        builder.Ignore(m => m.DiskPercent);

        // The shape every read uses: one server's recent history, newest first.
        builder.HasIndex(m => new { m.ServerId, m.CapturedAt }).IsDescending(false, true);

        // The retention sweep deletes by time across all servers, so it needs its
        // own index rather than the composite above. This is the largest table in
        // the schema and the one query that must stay fast as it grows.
        builder.HasIndex(m => m.CapturedAt);

        builder.HasOne<Server>()
            .WithMany()
            .HasForeignKey(m => m.ServerId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class AlertConfiguration : IEntityTypeConfiguration<Alert>
{
    public void Configure(EntityTypeBuilder<Alert> builder)
    {
        builder.ToTable("alerts");

        builder.HasKey(a => a.Id);

        builder.Property(a => a.Source).HasConversion<string>().HasMaxLength(30).IsRequired();
        builder.Property(a => a.Severity).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(a => a.RuleKey).HasMaxLength(100).IsRequired();
        builder.Property(a => a.Message).HasMaxLength(1000).IsRequired();

        builder.Ignore(a => a.IsOpen);

        // Deduplication reads this on every evaluation pass, for every server.
        // Without it the sweep degrades as the alert history grows, which is
        // exactly when it matters most.
        builder.HasIndex(a => new { a.RuleKey, a.SourceId, a.ResolvedAt });

        builder.HasIndex(a => new { a.Severity, a.RaisedAt }).IsDescending(false, true);
        builder.HasIndex(a => a.CustomerId);
    }
}
