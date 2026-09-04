using Knight.Application.Abstractions.ControlPlane;
using Knight.Application.Abstractions.Time;
using Knight.Application.Exceptions;
using Microsoft.Extensions.Options;
using Subscriptions.Domain;

namespace Subscriptions;

/// <summary>
/// Subscription lifecycle.
///
/// Two rules live here rather than in the aggregate because answering them means
/// reading things the aggregate cannot see: whether the plan lets the customer
/// choose a feature, and whether the customer's infrastructure can run it. Both
/// are refused outright rather than accepted and then quietly ignored, so a
/// customer is never billed for something they will not receive.
///
/// Every mutating path finishes by reconciling entitlements, so the commercial
/// facts never describe a subscription that has since moved on.
/// </summary>
internal sealed class SubscriptionService : ISubscriptionService
{
    private const int MaxPageSize = 100;

    private readonly ISubscriptionRepository _subscriptions;
    private readonly IPlanCatalogReader _plans;
    private readonly IFeatureCatalogReader _features;
    private readonly IStoreHostingReader _hosting;
    private readonly IPricingReader _pricing;
    private readonly IEntitlementService _entitlements;
    private readonly IAuditTrail _audit;
    private readonly IAuditContext _actor;
    private readonly IDateTimeProvider _clock;
    private readonly SubscriptionOptions _options;

    public SubscriptionService(
        ISubscriptionRepository subscriptions,
        IPlanCatalogReader plans,
        IFeatureCatalogReader features,
        IStoreHostingReader hosting,
        IPricingReader pricing,
        IEntitlementService entitlements,
        IAuditTrail audit,
        IAuditContext actor,
        IDateTimeProvider clock,
        IOptions<SubscriptionOptions> options)
    {
        _subscriptions = subscriptions;
        _plans = plans;
        _features = features;
        _hosting = hosting;
        _pricing = pricing;
        _entitlements = entitlements;
        _audit = audit;
        _actor = actor;
        _clock = clock;
        _options = options.Value;
    }

    public async Task<Subscription> StartAsync(StartSubscriptionInput input, CancellationToken cancellationToken)
    {
        var now = _clock.UtcNow;

        // One running subscription per customer. Two would make "what is this
        // customer entitled to?" a question with two answers.
        if (await _subscriptions.GetActiveForCustomerAsync(input.CustomerId, cancellationToken) is not null)
        {
            throw new ConflictException("The customer already has a subscription that has not been cancelled.");
        }

        var plan = await RequirePlanAsync(input.PlanId, cancellationToken);

        var subscription = Subscription.Start(
            Guid.NewGuid(),
            now,
            input.CustomerId,
            input.PlanId,
            now,
            now.Add(_options.BillingPeriod),
            input.AsTrial);

        await _subscriptions.AddAsync(subscription, cancellationToken);
        await ApplySelectionAsync(subscription, plan, input.FeatureIds, now, cancellationToken);
        await _subscriptions.SaveChangesAsync(cancellationToken);

        await _entitlements.ReconcileAsync(subscription.CustomerId, cancellationToken);
        await AuditAsync("subscription.started", subscription, cancellationToken);

        return subscription;
    }

    public Task<Subscription?> GetAsync(Guid id, CancellationToken cancellationToken) =>
        _subscriptions.GetByIdAsync(id, cancellationToken);

    public async Task<SubscriptionPage> ListAsync(SubscriptionListQuery query, CancellationToken cancellationToken)
    {
        var page = query.Page < 1 ? 1 : query.Page;
        var pageSize = query.PageSize is < 1 or > MaxPageSize ? 25 : query.PageSize;

        var (items, total) = await _subscriptions.ListAsync(page, pageSize, query.CustomerId, query.Status, cancellationToken);
        return new SubscriptionPage(items, page, pageSize, total);
    }

    public async Task<Subscription> ChangePlanAsync(Guid id, Guid planId, CancellationToken cancellationToken)
    {
        var subscription = await RequireAsync(id, cancellationToken);
        var before = Snapshot(subscription);

        await RequirePlanAsync(planId, cancellationToken);

        subscription.ChangePlan(planId, _clock.UtcNow);
        await _subscriptions.SaveChangesAsync(cancellationToken);

        // The selection was cleared with the plan change, so reconciliation
        // grants exactly what the new plan includes and nothing else.
        await _entitlements.ReconcileAsync(subscription.CustomerId, cancellationToken);
        await AuditAsync("subscription.plan_changed", subscription, cancellationToken, before);

        return subscription;
    }

    public async Task<Subscription> SetFeaturesAsync(
        Guid id,
        IReadOnlyCollection<Guid> featureIds,
        CancellationToken cancellationToken)
    {
        var subscription = await RequireAsync(id, cancellationToken);
        var before = Snapshot(subscription);
        var now = _clock.UtcNow;

        var plan = await RequirePlanAsync(subscription.PlanId, cancellationToken);

        await ApplySelectionAsync(subscription, plan, featureIds, now, cancellationToken);
        await _subscriptions.SaveChangesAsync(cancellationToken);

        await _entitlements.ReconcileAsync(subscription.CustomerId, cancellationToken);
        await AuditAsync("subscription.features_changed", subscription, cancellationToken, before);

        return subscription;
    }

    public Task<Subscription> CancelAsync(Guid id, CancellationToken cancellationToken) =>
        TransitionAsync(id, "subscription.cancelled", (subscription, now) => subscription.Cancel(now), cancellationToken);

    public Task<Subscription> RequestCancelAtPeriodEndAsync(Guid id, CancellationToken cancellationToken) =>
        TransitionAsync(id, "subscription.cancel_requested", (subscription, now) => subscription.RequestCancelAtPeriodEnd(now), cancellationToken);

    public Task<Subscription> SuspendAsync(Guid id, CancellationToken cancellationToken) =>
        TransitionAsync(id, "subscription.suspended", (subscription, now) => subscription.Suspend(now), cancellationToken);

    public Task<Subscription> ActivateAsync(Guid id, CancellationToken cancellationToken) =>
        TransitionAsync(id, "subscription.activated", (subscription, now) => subscription.Activate(now), cancellationToken);

    public async Task<QuoteView> QuoteAsync(
        Guid planId,
        IReadOnlyCollection<Guid> featureIds,
        CancellationToken cancellationToken)
    {
        await RequirePlanAsync(planId, cancellationToken);

        var quoted = await _pricing.QuoteAsync(planId, featureIds.Distinct().ToArray(), _clock.UtcNow, cancellationToken);

        return new QuoteView(
            quoted.Currency,
            quoted.Subtotal,
            quoted.Lines
                .Select(line => new QuoteLineView(line.Description, line.FeatureId, line.UnitPrice, line.Quantity, line.Total))
                .ToArray());
    }

    /// <summary>
    /// Brings the selection in line with exactly <paramref name="featureIds"/>,
    /// refusing anything the plan does not offer the customer or their
    /// infrastructure cannot run.
    /// </summary>
    private async Task ApplySelectionAsync(
        Subscription subscription,
        PlanOffering plan,
        IReadOnlyCollection<Guid> featureIds,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var requested = featureIds.Distinct().ToArray();
        var descriptors = await _features.GetManyAsync(requested, cancellationToken);

        foreach (var featureId in requested)
        {
            var offering = plan.Find(featureId)
                ?? throw new ConflictException($"Plan '{plan.Key}' does not offer feature '{featureId}'.");

            // Included features need no selecting; toggling one off is a plan
            // change, not a customer choice.
            if (!offering.IsCustomerToggleable)
            {
                throw new ConflictException(
                    $"Feature '{featureId}' is not customer-toggleable on plan '{plan.Key}' and cannot be selected.");
            }

            var feature = descriptors.SingleOrDefault(candidate => candidate.FeatureId == featureId)
                ?? throw new NotFoundException($"Feature '{featureId}' was not found.");

            if (!feature.CanBeEntitled)
            {
                throw new ConflictException($"Feature '{feature.Slug}' is {feature.Status.ToLowerInvariant()} and cannot be selected.");
            }

            if (feature.RequiresDedicatedInfrastructure &&
                !await _hosting.HasDedicatedCapacityAsync(subscription.CustomerId, cancellationToken))
            {
                throw new ConflictException(
                    $"Feature '{feature.Slug}' requires dedicated infrastructure and the customer has no store off shared hosting.");
            }
        }

        foreach (var featureId in subscription.EnabledFeatureIds.Where(existing => !requested.Contains(existing)))
        {
            subscription.DisableFeature(featureId, now);
        }

        foreach (var featureId in requested.Where(featureId => !subscription.HasFeatureEnabled(featureId)))
        {
            var selection = subscription.EnableFeature(featureId, _actor.ActorUserId, now);
            _subscriptions.RegisterNewFeature(selection);
        }
    }

    private async Task<Subscription> TransitionAsync(
        Guid id,
        string action,
        Action<Subscription, DateTimeOffset> transition,
        CancellationToken cancellationToken)
    {
        var subscription = await RequireAsync(id, cancellationToken);
        var before = Snapshot(subscription);

        transition(subscription, _clock.UtcNow);
        await _subscriptions.SaveChangesAsync(cancellationToken);

        // Suspension and cancellation stop entitling; activation starts again.
        // Reconciliation is what turns either into the actual entitlement rows.
        await _entitlements.ReconcileAsync(subscription.CustomerId, cancellationToken);
        await AuditAsync(action, subscription, cancellationToken, before);

        return subscription;
    }

    private async Task<PlanOffering> RequirePlanAsync(Guid planId, CancellationToken cancellationToken) =>
        await _plans.GetOfferingAsync(planId, cancellationToken)
        ?? throw new NotFoundException($"Plan '{planId}' was not found.");

    private async Task<Subscription> RequireAsync(Guid id, CancellationToken cancellationToken) =>
        await _subscriptions.GetByIdAsync(id, cancellationToken)
        ?? throw new NotFoundException($"Subscription '{id}' was not found.");

    private Task AuditAsync(
        string action,
        Subscription subscription,
        CancellationToken cancellationToken,
        object? before = null) =>
        _audit.RecordAsync(
            action,
            nameof(Subscription),
            subscription.Id.ToString(),
            subscription.CustomerId,
            cancellationToken,
            before,
            Snapshot(subscription));

    private static object Snapshot(Subscription subscription) => new
    {
        subscription.PlanId,
        Status = subscription.Status.ToString(),
        subscription.CurrentPeriodStart,
        subscription.CurrentPeriodEnd,
        EnabledFeatureIds = subscription.EnabledFeatureIds,
    };
}
