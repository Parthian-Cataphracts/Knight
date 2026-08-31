using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Provisioning.Domain;
using Stores.Domain;

namespace Knight.Infrastructure.ControlPlane.Configurations;

internal sealed class ProvisioningJobConfiguration : IEntityTypeConfiguration<ProvisioningJob>
{
    public void Configure(EntityTypeBuilder<ProvisioningJob> builder)
    {
        builder.ToTable("provisioning_jobs");

        builder.HasKey(job => job.Id);

        builder.Property(job => job.Kind).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(job => job.State).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(job => job.IdempotencyKey).HasMaxLength(200).IsRequired();
        builder.Property(job => job.CorrelationId).HasMaxLength(100).IsRequired();
        builder.Property(job => job.BaseImageVersion).HasMaxLength(50);
        builder.Property(job => job.FailureCode).HasMaxLength(100);
        builder.Property(job => job.FailureMessage).HasMaxLength(2000);
        builder.Property(job => job.FailureClass).HasConversion<string>().HasMaxLength(30);

        builder.Ignore(job => job.TotalStepCount);
        builder.Ignore(job => job.CompletedStepCount);
        builder.Ignore(job => job.IsFinished);
        builder.Ignore(job => job.IsAwaitingOperator);

        // The same start request arriving twice must produce one run, enforced
        // here rather than in the service: two concurrent calls would both find
        // nothing and both insert.
        builder.HasIndex(job => new { job.StoreId, job.IdempotencyKey }).IsUnique();

        // What the coordinator sweeps, and what the dashboard lists.
        builder.HasIndex(job => new { job.State, job.UpdatedAt });
        builder.HasIndex(job => new { job.CustomerId, job.CreatedAt }).IsDescending(false, true);

        builder.HasOne<Store>()
            .WithMany()
            .HasForeignKey(job => job.StoreId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasMany(job => job.Steps)
            .WithOne()
            .HasForeignKey(step => step.JobId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Metadata.FindNavigation(nameof(ProvisioningJob.Steps))!
            .SetPropertyAccessMode(PropertyAccessMode.Field);
    }
}

internal sealed class ProvisioningStepResultConfiguration : IEntityTypeConfiguration<ProvisioningStepResult>
{
    public void Configure(EntityTypeBuilder<ProvisioningStepResult> builder)
    {
        builder.ToTable("provisioning_job_steps");

        builder.HasKey(step => step.Id);

        builder.Property(step => step.Name).HasMaxLength(100).IsRequired();
        builder.Property(step => step.Mode).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(step => step.Status).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(step => step.Detail).HasMaxLength(ProvisioningStepResult.MaxDetailLength);
        builder.Property(step => step.ErrorCode).HasMaxLength(100);

        // One row per step per job: a re-evaluated step updates its row rather
        // than adding one, so progress stays truthful across a hundred passes.
        builder.HasIndex(step => new { step.JobId, step.Name }).IsUnique();
    }
}
