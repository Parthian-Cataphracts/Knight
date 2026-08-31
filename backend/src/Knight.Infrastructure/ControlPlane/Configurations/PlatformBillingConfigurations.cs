using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using PlatformBilling.Domain;

namespace Knight.Infrastructure.ControlPlane.Configurations;

/// <summary>
/// Mapping for KNIGHT's own billing — merchant → KNIGHT. Deliberately its own
/// tables, separate from the agency <c>Billing</c> invoices and from any store's
/// payment gateway (docs/self-service-saas-plan.md §3).
/// </summary>
internal sealed class PlatformBillingTransactionConfiguration : IEntityTypeConfiguration<PlatformBillingTransaction>
{
    public void Configure(EntityTypeBuilder<PlatformBillingTransaction> builder)
    {
        builder.ToTable("platform_billing_transactions");

        builder.HasKey(t => t.Id);

        builder.Property(t => t.Provider).HasMaxLength(50).IsRequired();
        builder.Property(t => t.ProviderTransactionId).HasMaxLength(200);
        builder.Property(t => t.Amount).HasColumnType("numeric(18,2)").IsRequired();
        builder.Property(t => t.RefundedAmount).HasColumnType("numeric(18,2)").IsRequired();
        builder.Property(t => t.Currency).HasMaxLength(3).IsRequired();
        builder.Property(t => t.Status).HasConversion<string>().HasMaxLength(30).IsRequired();
        builder.Property(t => t.IdempotencyKey).HasMaxLength(200).IsRequired();

        builder.Ignore(t => t.Charged);

        builder.HasIndex(t => t.CustomerId);
        builder.HasIndex(t => t.SubscriptionId);

        // The webhook's safety net: one row per idempotency key, so a replay
        // conflicts on insert instead of charging or activating twice.
        builder.HasIndex(t => t.IdempotencyKey).IsUnique();
        builder.HasIndex(t => new { t.Provider, t.ProviderTransactionId });
    }
}

internal sealed class CheckoutSessionConfiguration : IEntityTypeConfiguration<CheckoutSession>
{
    public void Configure(EntityTypeBuilder<CheckoutSession> builder)
    {
        builder.ToTable("checkout_sessions");

        builder.HasKey(s => s.Id);

        builder.Property(s => s.Interval).HasConversion<string>().HasMaxLength(20).IsRequired();
        // A native uuid[] column: the selected optional features, dependencies
        // already folded in. A small, read-only set that always travels with the
        // session, so it needs no table of its own.
        builder.Property(s => s.SelectedFeatureIds).HasColumnType("uuid[]").IsRequired();
        builder.Property(s => s.Amount).HasColumnType("numeric(18,2)").IsRequired();
        builder.Property(s => s.Currency).HasMaxLength(3).IsRequired();
        builder.Property(s => s.Provider).HasMaxLength(50);
        builder.Property(s => s.ProviderSessionId).HasMaxLength(200);
        builder.Property(s => s.Status).HasConversion<string>().HasMaxLength(20).IsRequired();

        builder.Ignore(s => s.Total);

        builder.HasIndex(s => s.CustomerId);
        builder.HasIndex(s => s.SubscriptionId);
        builder.HasIndex(s => new { s.Provider, s.ProviderSessionId });
    }
}
