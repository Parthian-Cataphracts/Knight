using FeatureDelivery.Domain;
using FeatureRegistry.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Stores.Domain;

// The commercial mapping in this namespace already has a FeatureConfiguration —
// the EF configuration for the Feature entity. The delivery entity of the same
// name is aliased so the two never resolve to each other by accident.
using StoreFeatureConfiguration = FeatureDelivery.Domain.FeatureConfiguration;

namespace Knight.Infrastructure.ControlPlane.Configurations;

/// <summary>
/// Mapping for the feature registry's deployable half and for delivery: versions
/// and their dependencies, installations, jobs and their steps, and per-store
/// configuration.
///
/// Two conventions carry over from the integration tables. Documents composed
/// elsewhere — a manifest, a configuration payload — are <c>jsonb</c>, because
/// they are read back as documents and Postgres can index into them when
/// phase 5 needs to. And everything reachable per customer carries
/// <c>customer_id</c> even where a join could reach it, because the isolation
/// filter has to apply without one (docs/authorization.md §3).
///
/// The uniqueness constraints here are the ones that make the delivery model
/// hold: one version per (feature, version string), one installation per
/// (store, feature), one configuration per (store, feature), and one unfinished
/// job per store.
/// </summary>
internal sealed class FeatureVersionConfiguration : IEntityTypeConfiguration<FeatureVersion>
{
    public void Configure(EntityTypeBuilder<FeatureVersion> builder)
    {
        builder.ToTable("feature_versions");

        builder.HasKey(v => v.Id);

        builder.Property(v => v.Version).HasMaxLength(50).IsRequired();
        builder.Property(v => v.PackageReference).HasMaxLength(500).IsRequired();
        builder.Property(v => v.ArtifactDigest).HasMaxLength(64).IsRequired();
        builder.Property(v => v.Signature).HasMaxLength(1000).IsRequired();
        builder.Property(v => v.SigningKeyId).HasMaxLength(100).IsRequired();
        builder.Property(v => v.ManifestJson).HasColumnType("jsonb").IsRequired();
        builder.Property(v => v.Status).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(v => v.ReleaseNotes).HasMaxLength(4000);
        builder.Property(v => v.YankReason).HasMaxLength(1000);

        builder.Ignore(v => v.IsInstallable);
        builder.Ignore(v => v.SemanticVersion);

        // A version is immutable once published, so the identity of a release is
        // its (feature, version) pair and republishing the same one must collide
        // rather than quietly produce a second row.
        builder.HasIndex(v => new { v.FeatureId, v.Version }).IsUnique();
        builder.HasIndex(v => v.Status);

        // Revoking a signing key means yanking everything it ever signed, which
        // is a query that has to be fast enough to run during an incident.
        builder.HasIndex(v => v.SigningKeyId);

        builder.HasOne<Feature>()
            .WithMany()
            .HasForeignKey(v => v.FeatureId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasMany(v => v.Dependencies)
            .WithOne()
            .HasForeignKey(d => d.FeatureVersionId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Metadata.FindNavigation(nameof(FeatureVersion.Dependencies))!
            .SetPropertyAccessMode(PropertyAccessMode.Field);
    }
}

internal sealed class FeatureDependencyConfiguration : IEntityTypeConfiguration<FeatureDependency>
{
    public void Configure(EntityTypeBuilder<FeatureDependency> builder)
    {
        builder.ToTable("feature_dependencies");

        builder.HasKey(d => d.Id);

        builder.Property(d => d.DependsOnSlug).HasMaxLength(100).IsRequired();
        builder.Property(d => d.VersionRangeExpression).HasMaxLength(200).IsRequired();

        builder.Ignore(d => d.Range);

        // The target is a slug rather than a foreign key on purpose: a manifest
        // may name a Feature that is not registered yet, and "missing dependency"
        // is a far better error than a foreign key violation during publish.
        builder.HasIndex(d => d.DependsOnSlug);
        builder.HasIndex(d => new { d.FeatureVersionId, d.DependsOnSlug }).IsUnique();
    }
}

internal sealed class FeatureInstallationConfiguration : IEntityTypeConfiguration<FeatureInstallation>
{
    public void Configure(EntityTypeBuilder<FeatureInstallation> builder)
    {
        builder.ToTable("feature_installations");

        builder.HasKey(i => i.Id);

        builder.Property(i => i.FeatureSlug).HasMaxLength(100).IsRequired();
        builder.Property(i => i.State).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(i => i.InstalledVersion).HasMaxLength(50);
        builder.Property(i => i.TargetVersion).HasMaxLength(50);
        builder.Property(i => i.PreviousVersion).HasMaxLength(50);
        builder.Property(i => i.FailureCode).HasMaxLength(100);
        builder.Property(i => i.FailureMessage).HasMaxLength(2000);
        builder.Property(i => i.RollbackOutcome).HasConversion<string>().HasMaxLength(30).IsRequired();
        builder.Property(i => i.BlockingReason).HasMaxLength(1000);
        builder.Property(i => i.Health).HasConversion<string>().HasMaxLength(20).IsRequired();

        builder.Ignore(i => i.CanAcceptJob);
        builder.Ignore(i => i.IsServing);

        // A store has one installation row per Feature. Two would make "is it
        // installed" a question with two answers.
        builder.HasIndex(i => new { i.StoreId, i.FeatureId }).IsUnique();
        builder.HasIndex(i => new { i.CustomerId, i.State });

        // The retention sweep asks for exactly this.
        builder.HasIndex(i => i.DataRetainedUntil);

        builder.HasOne<Store>()
            .WithMany()
            .HasForeignKey(i => i.StoreId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne<Feature>()
            .WithMany()
            .HasForeignKey(i => i.FeatureId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class FeatureInstallationJobConfiguration : IEntityTypeConfiguration<FeatureInstallationJob>
{
    public void Configure(EntityTypeBuilder<FeatureInstallationJob> builder)
    {
        builder.ToTable("feature_installation_jobs");

        builder.HasKey(j => j.Id);

        builder.Property(j => j.FeatureSlug).HasMaxLength(100).IsRequired();
        builder.Property(j => j.Type).HasConversion<string>().HasMaxLength(30).IsRequired();
        builder.Property(j => j.State).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(j => j.TargetVersion).HasMaxLength(50);
        builder.Property(j => j.IdempotencyKey).HasMaxLength(200).IsRequired();
        builder.Property(j => j.CorrelationId).HasMaxLength(100).IsRequired();
        builder.Property(j => j.FailureCode).HasMaxLength(100);
        builder.Property(j => j.FailureMessage).HasMaxLength(2000);
        builder.Property(j => j.RollbackOutcome).HasConversion<string>().HasMaxLength(30).IsRequired();
        builder.Property(j => j.Trigger).HasConversion<string>().HasMaxLength(20).IsRequired();

        builder.Ignore(j => j.CompletedStepCount);
        builder.Ignore(j => j.IsFinished);

        // The same request arriving twice — a retried call, a redelivered
        // entitlement event — must produce one job. The database enforces that,
        // not the service, because two concurrent requests would otherwise both
        // find nothing and both insert.
        builder.HasIndex(j => new { j.StoreId, j.IdempotencyKey }).IsUnique();

        // The agent's poll and the timeout sweep are both (store, state) reads.
        builder.HasIndex(j => new { j.StoreId, j.State });
        builder.HasIndex(j => new { j.CustomerId, j.QueuedAt }).IsDescending(false, true);
        builder.HasIndex(j => j.ClaimExpiresAt);

        builder.HasOne<Store>()
            .WithMany()
            .HasForeignKey(j => j.StoreId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasMany(j => j.Steps)
            .WithOne()
            .HasForeignKey(s => s.JobId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Metadata.FindNavigation(nameof(FeatureInstallationJob.Steps))!
            .SetPropertyAccessMode(PropertyAccessMode.Field);
    }
}

internal sealed class JobStepResultConfiguration : IEntityTypeConfiguration<JobStepResult>
{
    public void Configure(EntityTypeBuilder<JobStepResult> builder)
    {
        builder.ToTable("feature_job_steps");

        builder.HasKey(s => s.Id);

        builder.Property(s => s.Name).HasMaxLength(100).IsRequired();
        builder.Property(s => s.Status).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(s => s.Output).HasMaxLength(JobStepResult.MaxOutputLength);
        builder.Property(s => s.ErrorCode).HasMaxLength(100);

        // One row per step per job: a repeat report updates its row rather than
        // adding one, so the progress count stays truthful.
        builder.HasIndex(s => new { s.JobId, s.Name }).IsUnique();
    }
}

internal sealed class StoreFeatureConfigurationConfiguration : IEntityTypeConfiguration<StoreFeatureConfiguration>
{
    public void Configure(EntityTypeBuilder<StoreFeatureConfiguration> builder)
    {
        builder.ToTable("feature_configurations");

        builder.HasKey(c => c.Id);

        builder.Property(c => c.ValuesJson).HasColumnType("jsonb").IsRequired();
        builder.Property(c => c.SecretNamesJson).HasColumnType("jsonb").IsRequired();

        // The sealed secret document is text rather than jsonb: it is ciphertext,
        // and inviting the database to parse or index it would be inviting
        // exactly the thing encryption is here to prevent.
        builder.Property(c => c.EncryptedSecretsJson).HasColumnType("text");

        builder.Ignore(c => c.HasDrifted);

        builder.HasIndex(c => new { c.StoreId, c.FeatureId }).IsUnique();

        builder.HasOne<Store>()
            .WithMany()
            .HasForeignKey(c => c.StoreId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
