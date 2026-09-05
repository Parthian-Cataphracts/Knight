using Billing.Domain;
using FeatureRegistry.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Plans.Domain;
using Subscriptions.Domain;

namespace Knight.Infrastructure.ControlPlane.Configurations;

/// <summary>
/// Mapping for the commercial half of the control plane: the feature catalogue,
/// plans and prices, subscriptions and entitlements, and billing.
///
/// Money is stored as numeric(18,2) with the currency beside it, never as a
/// floating-point number: an invoice total that is off by a fraction of a cent is
/// wrong, not approximately right.
/// </summary>
internal sealed class FeatureConfiguration : IEntityTypeConfiguration<Feature>
{
    public void Configure(EntityTypeBuilder<Feature> builder)
    {
        builder.ToTable("features");

        builder.HasKey(f => f.Id);

        builder.Property(f => f.Slug).HasMaxLength(100).IsRequired();
        builder.Property(f => f.Name).HasMaxLength(200).IsRequired();
        builder.Property(f => f.Description).HasMaxLength(2000);
        builder.Property(f => f.Category).HasMaxLength(50).IsRequired();
        builder.Property(f => f.Status).HasConversion<string>().HasMaxLength(20).IsRequired();

        // Null for a top-level Feature; set for a sub-feature of a composed
        // parent (adr/0037). No FK: a Feature is never hard-deleted (it is
        // withdrawn), so the referential rule that matters is enforced in the
        // service, and a self-referencing FK complicates seeding order.
        builder.Property(f => f.ParentFeatureId);

        // The slug is the package name, so it is unique platform-wide.
        builder.HasIndex(f => f.Slug).IsUnique();
        builder.HasIndex(f => f.Status);
        builder.HasIndex(f => f.ParentFeatureId);
    }
}

internal sealed class PlanConfiguration : IEntityTypeConfiguration<Plan>
{
    public void Configure(EntityTypeBuilder<Plan> builder)
    {
        builder.ToTable("plans");

        builder.HasKey(p => p.Id);

        builder.Property(p => p.Key).HasMaxLength(50).IsRequired();
        builder.Property(p => p.Name).HasMaxLength(200).IsRequired();
        builder.Property(p => p.Description).HasMaxLength(2000);
        builder.Property(p => p.BasePriceAmount).HasColumnType("numeric(18,2)").IsRequired();
        builder.Property(p => p.Currency).HasMaxLength(3).IsRequired();

        builder.HasIndex(p => p.Key).IsUnique();

        builder.HasMany(p => p.Features)
            .WithOne()
            .HasForeignKey(f => f.PlanId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Metadata.FindNavigation(nameof(Plan.Features))!
            .SetPropertyAccessMode(PropertyAccessMode.Field);
    }
}

internal sealed class PlanFeatureConfiguration : IEntityTypeConfiguration<PlanFeature>
{
    public void Configure(EntityTypeBuilder<PlanFeature> builder)
    {
        builder.ToTable("plan_features");

        builder.HasKey(f => new { f.PlanId, f.FeatureId });

        builder.Property(f => f.PinnedVersionRange).HasMaxLength(100);

        builder.HasOne<Feature>()
            .WithMany()
            .HasForeignKey(f => f.FeatureId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class FeaturePriceConfiguration : IEntityTypeConfiguration<FeaturePrice>
{
    public void Configure(EntityTypeBuilder<FeaturePrice> builder)
    {
        builder.ToTable("feature_prices");

        builder.HasKey(p => p.Id);

        builder.Property(p => p.Amount).HasColumnType("numeric(18,2)").IsRequired();
        builder.Property(p => p.Currency).HasMaxLength(3).IsRequired();
        builder.Property(p => p.BillingPeriod).HasConversion<string>().HasMaxLength(20).IsRequired();

        // Prices are looked up by feature and moment on every quote.
        builder.HasIndex(p => new { p.FeatureId, p.PlanId, p.ValidFrom });

        builder.HasOne<Feature>()
            .WithMany()
            .HasForeignKey(p => p.FeatureId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class SubscriptionConfiguration : IEntityTypeConfiguration<Subscription>
{
    public void Configure(EntityTypeBuilder<Subscription> builder)
    {
        builder.ToTable("subscriptions");

        builder.HasKey(s => s.Id);

        builder.Property(s => s.Status).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(s => s.Provider).HasMaxLength(50);
        builder.Property(s => s.ProviderSubscriptionId).HasMaxLength(200);

        builder.HasIndex(s => s.CustomerId);
        builder.HasIndex(s => s.Status);
        builder.HasIndex(s => new { s.Provider, s.ProviderSubscriptionId });

        builder.HasOne<Plan>()
            .WithMany()
            .HasForeignKey(s => s.PlanId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasMany(s => s.Features)
            .WithOne()
            .HasForeignKey(f => f.SubscriptionId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Metadata.FindNavigation(nameof(Subscription.Features))!
            .SetPropertyAccessMode(PropertyAccessMode.Field);
    }
}

internal sealed class SubscriptionFeatureConfiguration : IEntityTypeConfiguration<SubscriptionFeature>
{
    public void Configure(EntityTypeBuilder<SubscriptionFeature> builder)
    {
        builder.ToTable("subscription_features");

        builder.HasKey(f => f.Id);

        // One row per feature per subscription; enabling and disabling flips the
        // flag rather than adding history rows.
        builder.HasIndex(f => new { f.SubscriptionId, f.FeatureId }).IsUnique();

        builder.HasOne<Feature>()
            .WithMany()
            .HasForeignKey(f => f.FeatureId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class FeatureEntitlementConfiguration : IEntityTypeConfiguration<FeatureEntitlement>
{
    public void Configure(EntityTypeBuilder<FeatureEntitlement> builder)
    {
        builder.ToTable("feature_entitlements");

        builder.HasKey(e => e.Id);

        builder.Property(e => e.Source).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(e => e.RevokedReason).HasMaxLength(200);

        // "What is this customer entitled to right now?" is the hottest question
        // in the system: every ingestion call and every delivery decision asks it.
        builder.HasIndex(e => new { e.CustomerId, e.FeatureId, e.RevokedAt });

        builder.HasOne<Feature>()
            .WithMany()
            .HasForeignKey(e => e.FeatureId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class BillingAccountConfiguration : IEntityTypeConfiguration<BillingAccount>
{
    public void Configure(EntityTypeBuilder<BillingAccount> builder)
    {
        builder.ToTable("billing_accounts");

        builder.HasKey(a => a.Id);

        builder.Property(a => a.Currency).HasMaxLength(3).IsRequired();
        builder.Property(a => a.BillingEmail).HasMaxLength(320).IsRequired();
        builder.Property(a => a.TaxId).HasMaxLength(50);

        // One account per customer.
        builder.HasIndex(a => a.CustomerId).IsUnique();
    }
}

internal sealed class InvoiceConfiguration : IEntityTypeConfiguration<Invoice>
{
    public void Configure(EntityTypeBuilder<Invoice> builder)
    {
        builder.ToTable("invoices");

        builder.HasKey(i => i.Id);

        builder.Property(i => i.Number).HasMaxLength(20);
        builder.Property(i => i.Currency).HasMaxLength(3).IsRequired();
        builder.Property(i => i.Status).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(i => i.Subtotal).HasColumnType("numeric(18,2)").IsRequired();
        builder.Property(i => i.Tax).HasColumnType("numeric(18,2)").IsRequired();
        builder.Property(i => i.Total).HasColumnType("numeric(18,2)").IsRequired();

        // Numbers are unique where assigned; drafts have none yet.
        builder.HasIndex(i => i.Number).IsUnique().HasFilter("\"Number\" IS NOT NULL");
        builder.HasIndex(i => new { i.CustomerId, i.Status });

        builder.HasMany(i => i.Lines)
            .WithOne()
            .HasForeignKey(l => l.InvoiceId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasMany(i => i.Payments)
            .WithOne()
            .HasForeignKey(p => p.InvoiceId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Metadata.FindNavigation(nameof(Invoice.Lines))!.SetPropertyAccessMode(PropertyAccessMode.Field);
        builder.Metadata.FindNavigation(nameof(Invoice.Payments))!.SetPropertyAccessMode(PropertyAccessMode.Field);
    }
}

internal sealed class InvoiceLineConfiguration : IEntityTypeConfiguration<InvoiceLine>
{
    public void Configure(EntityTypeBuilder<InvoiceLine> builder)
    {
        builder.ToTable("invoice_lines");

        builder.HasKey(l => l.Id);

        builder.Property(l => l.Description).HasMaxLength(300).IsRequired();
        builder.Property(l => l.UnitPrice).HasColumnType("numeric(18,2)").IsRequired();
        builder.Property(l => l.Total).HasColumnType("numeric(18,2)").IsRequired();
    }
}

internal sealed class PaymentRecordConfiguration : IEntityTypeConfiguration<PaymentRecord>
{
    public void Configure(EntityTypeBuilder<PaymentRecord> builder)
    {
        builder.ToTable("payment_records");

        builder.HasKey(p => p.Id);

        builder.Property(p => p.Amount).HasColumnType("numeric(18,2)").IsRequired();
        builder.Property(p => p.Currency).HasMaxLength(3).IsRequired();
        builder.Property(p => p.Method).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(p => p.Reference).HasMaxLength(200);

        builder.HasIndex(p => p.InvoiceId);
    }
}

internal sealed class InvoiceNumberSequenceConfiguration : IEntityTypeConfiguration<InvoiceNumberSequence>
{
    public void Configure(EntityTypeBuilder<InvoiceNumberSequence> builder)
    {
        builder.ToTable("invoice_number_sequences");

        builder.HasKey(s => s.Year);

        builder.Property(s => s.Year).ValueGeneratedNever();
        builder.Property(s => s.LastNumber).IsRequired();
    }
}
