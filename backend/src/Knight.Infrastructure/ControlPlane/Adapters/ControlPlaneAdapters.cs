using FeatureRegistry.Domain;
using Knight.Application.Abstractions.ControlPlane;
using Knight.Application.Exceptions;
using Microsoft.Extensions.Logging;
using Plans;
using Plans.Domain;
using Stores.Domain;
using Subscriptions.Domain;

namespace Knight.Infrastructure.ControlPlane.Adapters;

/// <summary>
/// Adapters that let one control-plane module use another's data without either
/// referencing the other. The modules declare ports in the application layer;
/// these are the implementations, and Infrastructure is the only place that knows
/// both sides — the same pattern the store-side modules already use.
/// </summary>
internal sealed class PlanCatalogReader : IPlanCatalogReader
{
    private readonly IPlanRepository _plans;

    public PlanCatalogReader(IPlanRepository plans)
    {
        _plans = plans;
    }

    public async Task<PlanOffering?> GetOfferingAsync(Guid planId, CancellationToken cancellationToken)
    {
        var plan = await _plans.GetByIdAsync(planId, cancellationToken);

        return plan is null
            ? null
            : new PlanOffering(
                plan.Id,
                plan.Key,
                plan.Name,
                plan.Features
                    .Select(feature => new PlanFeatureOffering(
                        feature.FeatureId,
                        feature.IsIncluded,
                        feature.IsCustomerToggleable,
                        feature.PinnedVersionRange))
                    .ToArray());
    }
}

internal sealed class FeatureCatalogReader : IFeatureCatalogReader
{
    private readonly IFeatureRepository _features;

    public FeatureCatalogReader(IFeatureRepository features)
    {
        _features = features;
    }

    public async Task<FeatureDescriptor?> GetAsync(Guid featureId, CancellationToken cancellationToken)
    {
        var feature = await _features.GetByIdAsync(featureId, cancellationToken);
        return feature is null ? null : Describe(feature);
    }

    public async Task<IReadOnlyCollection<FeatureDescriptor>> GetManyAsync(
        IReadOnlyCollection<Guid> featureIds,
        CancellationToken cancellationToken)
    {
        var features = await _features.GetManyAsync(featureIds, cancellationToken);
        return features.Select(Describe).ToArray();
    }

    private static FeatureDescriptor Describe(Feature feature) => new(
        feature.Id,
        feature.Slug,
        feature.Status.ToString(),
        feature.IsOptional,
        feature.RequiresDedicatedInfrastructure,
        feature.CanBeEntitled,
        feature.RemainsEntitled);
}

/// <summary>
/// Prices through the one calculator, so a quote shown to a customer and a line
/// on their invoice are produced by the same arithmetic.
/// </summary>
internal sealed class PricingReader : IPricingReader
{
    private readonly IPricingCalculator _calculator;
    private readonly IPlanRepository _plans;

    public PricingReader(IPricingCalculator calculator, IPlanRepository plans)
    {
        _calculator = calculator;
        _plans = plans;
    }

    public async Task<QuotedPrice> QuoteAsync(
        Guid planId,
        IReadOnlyCollection<Guid> featureIds,
        DateTimeOffset moment,
        CancellationToken cancellationToken)
    {
        var plan = await _plans.GetByIdAsync(planId, cancellationToken)
            ?? throw new NotFoundException($"Plan '{planId}' was not found.");

        var quote = await _calculator.QuoteAsync(new QuoteRequest(plan, featureIds, moment), cancellationToken);

        return new QuotedPrice(
            quote.Currency,
            quote.Subtotal.Amount,
            quote.Lines
                .Select(line => new QuotedLine(
                    line.Description,
                    line.FeatureId,
                    line.Quantity,
                    line.UnitPrice.Amount,
                    line.Total.Amount))
                .ToArray());
    }
}

internal sealed class SubscriptionReader : ISubscriptionReader
{
    private readonly ISubscriptionRepository _subscriptions;

    public SubscriptionReader(ISubscriptionRepository subscriptions)
    {
        _subscriptions = subscriptions;
    }

    public async Task<SubscriptionSnapshot?> GetAsync(Guid subscriptionId, CancellationToken cancellationToken)
    {
        var subscription = await _subscriptions.GetByIdAsync(subscriptionId, cancellationToken);

        return subscription is null
            ? null
            : new SubscriptionSnapshot(
                subscription.Id,
                subscription.CustomerId,
                subscription.PlanId,
                subscription.Status.ToString(),
                subscription.EnabledFeatureIds,
                subscription.CurrentPeriodStart,
                subscription.CurrentPeriodEnd);
    }
}

/// <summary>
/// Answers whether a customer has anywhere to run a capability that cannot live
/// on shared hosting.
/// </summary>
internal sealed class StoreHostingReader : IStoreHostingReader
{
    private readonly IStoreRepository _stores;

    public StoreHostingReader(IStoreRepository stores)
    {
        _stores = stores;
    }

    public async Task<bool> HasDedicatedCapacityAsync(Guid customerId, CancellationToken cancellationToken)
    {
        // Page size is a bound, not a filter: a customer with more stores than
        // this and no dedicated one among the first page would be answered
        // wrongly, so the query asks for all of them and stops at the first hit.
        var (stores, _) = await _stores.ListAsync(1, int.MaxValue, customerId, null, null, cancellationToken);

        return stores.Any(store => store.HostingModel is not HostingModel.SharedManaged);
    }
}

/// <summary>
/// Records entitlement changes to the log until phase 3.5's delivery engine
/// consumes them.
///
/// This is deliberately not a queue. Publishing to something durable before
/// anything reads it would invent a contract that the delivery engine has not
/// been designed against yet; what matters now is that the events exist, are
/// raised at exactly the right moments, and are visible when the engine arrives.
/// </summary>
internal sealed class LoggingEntitlementEventPublisher : IEntitlementEventPublisher
{
    private readonly ILogger<LoggingEntitlementEventPublisher> _logger;

    public LoggingEntitlementEventPublisher(ILogger<LoggingEntitlementEventPublisher> logger)
    {
        _logger = logger;
    }

    public Task PublishAsync(FeatureEntitlementGranted @event, CancellationToken cancellationToken)
    {
        _logger.LogInformation(
            "Entitlement granted: customer {CustomerId} feature {FeatureId} source {Source} at {OccurredAt}",
            @event.CustomerId,
            @event.FeatureId,
            @event.Source,
            @event.OccurredAt);

        return Task.CompletedTask;
    }

    public Task PublishAsync(FeatureEntitlementRevoked @event, CancellationToken cancellationToken)
    {
        // Consumers must read this as "disable the installed feature", never as
        // "uninstall it" (docs/adr/0016).
        _logger.LogInformation(
            "Entitlement revoked: customer {CustomerId} feature {FeatureId} reason {Reason} at {OccurredAt}",
            @event.CustomerId,
            @event.FeatureId,
            @event.Reason,
            @event.OccurredAt);

        return Task.CompletedTask;
    }
}
