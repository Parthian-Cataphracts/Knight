namespace Knight.Application.Abstractions.ControlPlane;

/// <summary>
/// What one plan offers, flattened to exactly what an entitlement decision
/// needs. A port rather than a module reference: the module that owns
/// entitlements and the module that owns plans stay independent of each other,
/// the same way the store-side modules already talk through readers.
/// </summary>
public sealed record PlanOffering(
    Guid PlanId,
    string Key,
    string Name,
    IReadOnlyCollection<PlanFeatureOffering> Features)
{
    public PlanFeatureOffering? Find(Guid featureId) =>
        Features.SingleOrDefault(feature => feature.FeatureId == featureId);

    public IReadOnlyCollection<Guid> IncludedFeatureIds =>
        Features.Where(feature => feature.IsIncluded).Select(feature => feature.FeatureId).ToArray();
}

public sealed record PlanFeatureOffering(
    Guid FeatureId,
    bool IsIncluded,
    bool IsCustomerToggleable,
    string? PinnedVersionRange);

public interface IPlanCatalogReader
{
    Task<PlanOffering?> GetOfferingAsync(Guid planId, CancellationToken cancellationToken);
}

/// <summary>
/// The commercial facts about a feature that entitlement decisions turn on.
/// Deliberately excludes versions, manifests and artifacts: those belong to
/// delivery, which is a separate question from whether the customer is owed the
/// capability at all.
/// </summary>
public sealed record FeatureDescriptor(
    Guid FeatureId,
    string Slug,
    string Status,
    bool IsOptional,
    bool RequiresDedicatedInfrastructure,
    bool CanBeEntitled,
    bool RemainsEntitled);

public interface IFeatureCatalogReader
{
    Task<FeatureDescriptor?> GetAsync(Guid featureId, CancellationToken cancellationToken);

    Task<IReadOnlyCollection<FeatureDescriptor>> GetManyAsync(
        IReadOnlyCollection<Guid> featureIds,
        CancellationToken cancellationToken);
}

public sealed record QuotedLine(string Description, Guid? FeatureId, int Quantity, decimal UnitPrice, decimal Total);

public sealed record QuotedPrice(string Currency, decimal Subtotal, IReadOnlyCollection<QuotedLine> Lines);

/// <summary>
/// Prices a plan plus a feature selection. A port over the pricing calculator so
/// subscriptions and billing can both quote without either of them depending on
/// the module that owns the price list — and so there is still exactly one
/// implementation doing the arithmetic.
/// </summary>
public interface IPricingReader
{
    Task<QuotedPrice> QuoteAsync(
        Guid planId,
        IReadOnlyCollection<Guid> featureIds,
        DateTimeOffset moment,
        CancellationToken cancellationToken);
}

/// <summary>
/// What billing needs to know about a subscription, flattened. A port for the
/// same reason as the others: billing prices what a subscription bought without
/// depending on the module that owns it.
/// </summary>
public sealed record SubscriptionSnapshot(
    Guid SubscriptionId,
    Guid CustomerId,
    Guid PlanId,
    string Status,
    IReadOnlyCollection<Guid> EnabledFeatureIds,
    DateTimeOffset CurrentPeriodStart,
    DateTimeOffset CurrentPeriodEnd);

public interface ISubscriptionReader
{
    Task<SubscriptionSnapshot?> GetAsync(Guid subscriptionId, CancellationToken cancellationToken);

    /// <summary>
    /// Active subscriptions whose current period has ended by <paramref name="asOf"/>
    /// — the billing run's input.
    ///
    /// Only active ones. A cancelled or suspended subscription must not keep
    /// generating invoices, and "we billed them for three months after they left"
    /// is the kind of mistake that is discovered by the customer rather than by
    /// us.
    /// </summary>
    Task<IReadOnlyCollection<SubscriptionSnapshot>> ListDueForBillingAsync(
        DateTimeOffset asOf,
        CancellationToken cancellationToken);
}

/// <summary>
/// Rolls a subscription's billing period forward once it has been invoiced.
///
/// A port rather than a module reference: billing must not depend on the module
/// that owns subscriptions, and the period belongs to the subscription rather
/// than to the invoice. Separating them also makes the ordering explicit — the
/// period is only advanced *after* an invoice for the old one exists, so a
/// failure half way leaves a period that will be billed again rather than one
/// that never was.
/// </summary>
public interface ISubscriptionPeriodWriter
{
    Task AdvancePeriodAsync(Guid subscriptionId, DateTimeOffset newPeriodEnd, CancellationToken cancellationToken);
}
