using Ingestion.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Stores.Domain;

namespace Knight.Infrastructure.ControlPlane.Configurations;

/// <summary>
/// Mapping for everything the store link produces: health observations,
/// deployments, and the telemetry stores push.
///
/// Two conventions run through all of it. Payloads a store composed are stored
/// as <c>jsonb</c> — they are read back as documents, and Postgres can index
/// into them when phase 5 needs to — while everything KNIGHT itself decides is a
/// column. And every table carries <c>customer_id</c> even where it could be
/// reached through the store, because the isolation filter has to be able to
/// apply without a join (docs/authorization.md §3).
///
/// These tables are append-only and grow with traffic rather than with the
/// number of customers, so each one is indexed on (store, time descending) —
/// the shape every screen and every retention job reads them in.
/// </summary>
internal sealed class StoreHealthCheckConfiguration : IEntityTypeConfiguration<StoreHealthCheck>
{
    public void Configure(EntityTypeBuilder<StoreHealthCheck> builder)
    {
        builder.ToTable("store_health_checks");

        builder.HasKey(c => c.Id);

        builder.Property(c => c.Status).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(c => c.Source).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(c => c.ReportedVersion).HasMaxLength(50);
        builder.Property(c => c.Dependencies).HasColumnType("jsonb");
        builder.Property(c => c.ReportedFeatures).HasColumnType("jsonb");
        builder.Property(c => c.Detail).HasMaxLength(500);

        builder.Ignore(c => c.IsHealthy);

        builder.HasIndex(c => new { c.StoreId, c.CheckedAt }).IsDescending(false, true);
        builder.HasIndex(c => c.CheckedAt);

        builder.HasOne<Store>()
            .WithMany()
            .HasForeignKey(c => c.StoreId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class StoreDeploymentConfiguration : IEntityTypeConfiguration<StoreDeployment>
{
    public void Configure(EntityTypeBuilder<StoreDeployment> builder)
    {
        builder.ToTable("store_deployments");

        builder.HasKey(d => d.Id);

        builder.Property(d => d.Version).HasMaxLength(50).IsRequired();
        builder.Property(d => d.PreviousVersion).HasMaxLength(50);
        builder.Property(d => d.Source).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(d => d.Status).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(d => d.Notes).HasMaxLength(2000);

        builder.HasIndex(d => new { d.StoreId, d.DeployedAt }).IsDescending(false, true);

        builder.HasOne<Store>()
            .WithMany()
            .HasForeignKey(d => d.StoreId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class StoreErrorEventConfiguration : IEntityTypeConfiguration<StoreErrorEvent>
{
    public void Configure(EntityTypeBuilder<StoreErrorEvent> builder)
    {
        builder.ToTable("store_error_events");

        builder.HasKey(e => e.Id);

        builder.Property(e => e.Environment).HasMaxLength(20).IsRequired();
        builder.Property(e => e.StoreVersion).HasMaxLength(50);
        builder.Property(e => e.ExceptionType).HasMaxLength(200).IsRequired();
        builder.Property(e => e.Message).HasMaxLength(StoreErrorEvent.MaxMessageLength).IsRequired();
        builder.Property(e => e.Endpoint).HasMaxLength(500);
        builder.Property(e => e.HttpMethod).HasMaxLength(10);
        builder.Property(e => e.StackTrace).HasMaxLength(StoreErrorEvent.MaxStackTraceLength);
        builder.Property(e => e.RequestId).HasMaxLength(100);
        builder.Property(e => e.TraceId).HasMaxLength(100);
        builder.Property(e => e.Context).HasColumnType("jsonb");

        builder.HasIndex(e => new { e.StoreId, e.OccurredAt }).IsDescending(false, true);
        builder.HasIndex(e => e.ErrorGroupId);

        builder.HasOne<Store>()
            .WithMany()
            .HasForeignKey(e => e.StoreId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class StoreLifecycleEventConfiguration : IEntityTypeConfiguration<StoreLifecycleEvent>
{
    public void Configure(EntityTypeBuilder<StoreLifecycleEvent> builder)
    {
        builder.ToTable("store_events");

        builder.HasKey(e => e.Id);

        builder.Property(e => e.Type).HasMaxLength(100).IsRequired();
        builder.Property(e => e.Severity).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(e => e.Summary).HasMaxLength(500).IsRequired();
        builder.Property(e => e.Environment).HasMaxLength(20).IsRequired();
        builder.Property(e => e.TraceId).HasMaxLength(100);
        builder.Property(e => e.Payload).HasColumnType("jsonb");

        builder.HasIndex(e => new { e.StoreId, e.OccurredAt }).IsDescending(false, true);
        builder.HasIndex(e => e.Type);

        builder.HasOne<Store>()
            .WithMany()
            .HasForeignKey(e => e.StoreId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class StoreLogEntryConfiguration : IEntityTypeConfiguration<StoreLogEntry>
{
    public void Configure(EntityTypeBuilder<StoreLogEntry> builder)
    {
        builder.ToTable("store_log_entries");

        builder.HasKey(e => e.Id);

        builder.Property(e => e.Level).HasMaxLength(20).IsRequired();
        builder.Property(e => e.Service).HasMaxLength(100);
        builder.Property(e => e.Environment).HasMaxLength(20).IsRequired();
        builder.Property(e => e.StoreVersion).HasMaxLength(50);
        builder.Property(e => e.RequestId).HasMaxLength(100);
        builder.Property(e => e.TraceId).HasMaxLength(100);
        builder.Property(e => e.Message).HasMaxLength(StoreLogEntry.MaxMessageLength).IsRequired();
        builder.Property(e => e.Exception).HasMaxLength(StoreErrorEvent.MaxStackTraceLength);
        builder.Property(e => e.Attributes).HasColumnType("jsonb");

        builder.HasIndex(e => new { e.StoreId, e.Timestamp }).IsDescending(false, true);
        builder.HasIndex(e => new { e.StoreId, e.Level });

        builder.HasOne<Store>()
            .WithMany()
            .HasForeignKey(e => e.StoreId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class StoreBackupConfiguration : IEntityTypeConfiguration<StoreBackup>
{
    public void Configure(EntityTypeBuilder<StoreBackup> builder)
    {
        builder.ToTable("store_backups");

        builder.HasKey(backup => backup.Id);

        builder.Property(backup => backup.Status).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(backup => backup.Kind).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(backup => backup.Location).HasMaxLength(StoreBackup.MaxLocationLength);
        builder.Property(backup => backup.Detail).HasMaxLength(1000);

        builder.Ignore(backup => backup.Duration);

        // Both reads this table serves: a store's backup history, newest first,
        // and "when did this store last succeed" for the overdue sweep.
        builder.HasIndex(backup => new { backup.StoreId, backup.StartedAt }).IsDescending(false, true);
        builder.HasIndex(backup => new { backup.StoreId, backup.Status, backup.CompletedAt });

        builder.HasOne<Store>()
            .WithMany()
            .HasForeignKey(backup => backup.StoreId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
