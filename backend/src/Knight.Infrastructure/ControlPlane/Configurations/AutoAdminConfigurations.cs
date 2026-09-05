using AutoAdmin.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Knight.Infrastructure.ControlPlane.Configurations;

/// <summary>
/// Mapping for the Automatic Admin engine (docs/adr/0038): a customer's autonomy
/// setting, and the content jobs with the drafts they generated and the
/// publications they produced. Both aggregate roots are customer-owned, so the
/// context's isolation filter keeps one customer from reading another's runs.
/// </summary>
internal sealed class AutoAdminSettingsConfiguration : IEntityTypeConfiguration<AutoAdminSettings>
{
    public void Configure(EntityTypeBuilder<AutoAdminSettings> builder)
    {
        builder.ToTable("auto_admin_settings");

        builder.HasKey(s => s.Id);

        builder.Property(s => s.Autonomy).HasConversion<string>().HasMaxLength(20).IsRequired();

        // One row per customer.
        builder.HasIndex(s => s.CustomerId).IsUnique();
    }
}

internal sealed class ContentJobConfiguration : IEntityTypeConfiguration<ContentJob>
{
    public void Configure(EntityTypeBuilder<ContentJob> builder)
    {
        builder.ToTable("content_jobs");

        builder.HasKey(j => j.Id);

        builder.Property(j => j.Topic).HasMaxLength(500).IsRequired();
        builder.Property(j => j.Autonomy).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(j => j.Status).HasConversion<string>().HasMaxLength(20).IsRequired();

        // "This customer's runs, newest first" is the portal's list query.
        builder.HasIndex(j => new { j.CustomerId, j.CreatedAt });

        builder.HasMany(j => j.Drafts)
            .WithOne()
            .HasForeignKey(d => d.ContentJobId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasMany(j => j.Publications)
            .WithOne()
            .HasForeignKey(p => p.ContentJobId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Metadata.FindNavigation(nameof(ContentJob.Drafts))!.SetPropertyAccessMode(PropertyAccessMode.Field);
        builder.Metadata.FindNavigation(nameof(ContentJob.Publications))!.SetPropertyAccessMode(PropertyAccessMode.Field);
    }
}

internal sealed class ContentDraftConfiguration : IEntityTypeConfiguration<ContentDraft>
{
    public void Configure(EntityTypeBuilder<ContentDraft> builder)
    {
        builder.ToTable("content_drafts");

        builder.HasKey(d => d.Id);

        builder.Property(d => d.Kind).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(d => d.Body).HasMaxLength(8000).IsRequired();
        builder.Property(d => d.GeneratorName).HasMaxLength(50).IsRequired();

        builder.HasIndex(d => d.ContentJobId);
    }
}

internal sealed class PublicationConfiguration : IEntityTypeConfiguration<Publication>
{
    public void Configure(EntityTypeBuilder<Publication> builder)
    {
        builder.ToTable("content_publications");

        builder.HasKey(p => p.Id);

        builder.Property(p => p.ChannelKey).HasMaxLength(50).IsRequired();
        builder.Property(p => p.Detail).HasMaxLength(1000).IsRequired();
        builder.Property(p => p.ExternalReference).HasMaxLength(200);
        builder.Property(p => p.PublisherName).HasMaxLength(50).IsRequired();

        builder.HasIndex(p => p.ContentJobId);
    }
}
