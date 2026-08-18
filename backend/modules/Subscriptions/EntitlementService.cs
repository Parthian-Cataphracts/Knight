using Knight.Application.Abstractions.ControlPlane;
using Knight.Application.Abstractions.Time;
using Knight.Application.Exceptions;
using Subscriptions.Domain;

namespace Subscriptions;

/// <summary>
/// Resolves and maintains entitlements. Plans and features are reached through
/// reader ports, so this module stays independent of the modules that own them.
///
/// Reconciliation is the interesting part. It is idempotent, and it is the only
/// thing that grants or revokes plan-derived entitlements, so the answer to "what
/// does this customer hold?" never depends on the order in which somebody clicked
/// things. Manual grants are outside its remit entirely: they were made
/// deliberately against the plan, and quietly withdrawing one because a plan
/// changed would undo a decision this service never made.
/// </summary>
internal sealed class EntitlementService : IEntitlementService
{
    private readonly IFeatureEntitlementRepository _entitlements;
    private readonly ISubscriptionRepository _subscriptions;
    private readonly IPlanCatalogReader _plans;
    private readonly IFeatureCatalogReader _features;
    private readonly IStoreHostingReader _hosting;
    private readonly IEntitlementEventPublisher _events;
    private readonly IAuditTrail _audit;
    private readonly IDateTimeProvider _clock;

    public EntitlementService(
        IFeatureEntitlementRepository entitlements,
        ISubscriptionRepository subscriptions,
        IPlanCatalogReader plans,
        IFeatureCatalogReader features,
        IStoreHostingReader hosting,
        IEntitlementEventPublisher events,
        IAuditTrail audit,
        IDateTimeProvider clock)
    {
        _entitlements = entitlements;
        _subscriptions = subscriptions;
        _plans = plans;
        _features = features;
        _hosting = hosting;
        _events = events;
        _audit = audit;
        _clock = clock;
    }

    public async Task<IReadOnlyCollection<EntitlementView>> ResolveForCustomerAsync(
        Guid customerId,
        bool includeInactive,
        CancellationToken cancellationToken)
    {
        var now = _clock.UtcNow;
        var held = await _entitlements.ListForCustomerAsync(customerId, includeInactive, cancellationToken);

        return held
            .Where(entitlement => includeInactive || entitlement.IsActiveAt(now))
            .Select(entitlement => Describe(entitlement, now))
            .ToArray();
    }

    public async Task<bool> IsEntitledAsync(Guid customerId, Guid featureId, CancellationToken cancellationToken)
    {
        var now = _clock.UtcNow;

        var entitlement = await _entitlements.FindActiveAsync(customerId, featureId, now, cancellationToken);
        if (entitlement is null)
        {
            return false;
        }

        // A withdrawn feature entitles nothing even where the row survives: the
        // capability no longer exists to be owed.
        var feature = await _features.GetAsync(featureId, cancellationToken);
        return feature is not null && feature.RemainsEntitled;
    }

    public async Task<EntitlementDecision> CanEntitleAsync(Guid customerId, Guid featureId, CancellationToken cancellationToken)
    {
        var feature = await _features.GetAsync(featureId, cancellationToken)
            ?? throw new NotFoundException($"Feature '{featureId}' was not found.");

        if (!feature.CanBeEntitled)
        {
            return new EntitlementDecision(
                EntitlementRefusal.FeatureNotAvailable,
                $"The feature is {feature.Status.ToLowerInvariant()}.");
        }

        if (!await CanRunAsync(customerId, feature, cancellationToken))
        {
            return new EntitlementDecision(
                EntitlementRefusal.RequiresDedicatedInfrastructure,
                "The feature needs dedicated infrastructure, and the customer has no store off shared hosting.");
        }

        var subscription = await _subscriptions.GetActiveForCustomerAsync(customerId, cancellationToken);
        if (subscription is null || !subscription.IsEntitling)
        {
            return new EntitlementDecision(
                EntitlementRefusal.NoEntitlingSubscription,
                "The customer has no subscription that entitles anything.");
        }

        var plan = await _plans.GetOfferingAsync(subscription.PlanId, cancellationToken)
            ?? throw new NotFoundException($"Plan '{subscription.PlanId}' was not found.");

        var offering = plan.Find(featureId);
        if (offering is null)
        {
            return new EntitlementDecision(EntitlementRefusal.NotOfferedByPlan, "The plan does not list this feature.");
        }

        // Included features are already the customer's; the question here is only
        // whether they may take one that is not.
        if (!offering.IsIncluded && !offering.IsCustomerToggleable)
        {
            return new EntitlementDecision(
                EntitlementRefusal.NotOfferedByPlan,
                "The plan lists this feature but does not let the customer switch it on.");
        }

        return EntitlementDecision.Allowed;
    }

    public async Task<EntitlementView> GrantAsync(
        Guid customerId,
        Guid featureId,
        Guid grantedBy,
        DateTimeOffset? expiresAt,
        CancellationToken cancellationToken)
    {
        var now = _clock.UtcNow;

        var feature = await _features.GetAsync(featureId, cancellationToken)
            ?? throw new NotFoundException($"Feature '{featureId}' was not found.");

        if (!feature.CanBeEntitled)
        {
            throw new ConflictException($"Feature '{feature.Slug}' is {feature.Status.ToLowerInvariant()} and cannot be granted.");
        }

        // The hosting rule holds for manual grants too. Platform staff may grant
        // outside a plan; they may not grant a capability that cannot run.
        if (!await CanRunAsync(customerId, feature, cancellationToken))
        {
            throw new ConflictException(
                $"Feature '{feature.Slug}' requires dedicated infrastructure and the customer has no store off shared hosting.");
        }

        var existing = await _entitlements.FindActiveAsync(customerId, featureId, now, cancellationToken);
        if (existing is not null)
        {
            if (expiresAt is not null)
            {
                existing.ExtendTo(expiresAt.Value);
                await _entitlements.SaveChangesAsync(cancellationToken);
            }

            return Describe(existing, now);
        }

        var entitlement = FeatureEntitlement.Grant(
            Guid.NewGuid(),
            customerId,
            featureId,
            EntitlementSource.Grant,
            now,
            expiresAt,
            grantedBy);

        await _entitlements.AddAsync(entitlement, cancellationToken);
        await _entitlements.SaveChangesAsync(cancellationToken);

        await AuditAsync("entitlement.granted", entitlement, feature.Slug, cancellationToken);
        await _events.PublishAsync(
            new FeatureEntitlementGranted(customerId, featureId, entitlement.Source.ToString(), now),
            cancellationToken);

        return Describe(entitlement, now);
    }

    public async Task RevokeAsync(Guid customerId, Guid featureId, string reason, CancellationToken cancellationToken)
    {
        var now = _clock.UtcNow;

        var entitlement = await _entitlements.FindActiveAsync(customerId, featureId, now, cancellationToken)
            ?? throw new NotFoundException("The customer holds no active entitlement for that feature.");

        entitlement.Revoke(now, reason);
        await _entitlements.SaveChangesAsync(cancellationToken);

        var feature = await _features.GetAsync(featureId, cancellationToken);
        await AuditAsync("entitlement.revoked", entitlement, feature?.Slug, cancellationToken);

        // Consumers must read this as "disable", never as "uninstall".
        await _events.PublishAsync(new FeatureEntitlementRevoked(customerId, featureId, reason, now), cancellationToken);
    }

    public async Task ReconcileAsync(Guid customerId, CancellationToken cancellationToken)
    {
        var now = _clock.UtcNow;

        var subscription = await _subscriptions.GetActiveForCustomerAsync(customerId, cancellationToken);
        var held = await _entitlements.ListForCustomerAsync(customerId, includeInactive: false, cancellationToken);
        var expected = await ResolveExpectedAsync(subscription, cancellationToken);

        // Reconciliation grants and revokes on the customer's behalf, so it is
        // audited exactly like a hand-made change: an entitlement that appeared
        // or vanished without a trail is indistinguishable from a bug
        // (docs/authorization.md section 7).
        var descriptors = await _features.GetManyAsync(
            held.Select(entitlement => entitlement.FeatureId).Concat(expected.Keys).Distinct().ToArray(),
            cancellationToken);

        string? SlugOf(Guid featureId) =>
            descriptors.SingleOrDefault(descriptor => descriptor.FeatureId == featureId)?.Slug;

        // Revoke what the customer no longer holds, leaving manual grants be.
        foreach (var entitlement in held.Where(candidate =>
                     candidate.Source is not EntitlementSource.Grant &&
                     candidate.IsActiveAt(now) &&
                     !expected.ContainsKey(candidate.FeatureId)))
        {
            entitlement.Revoke(now, "subscription_no_longer_grants");
            await AuditAsync("entitlement.revoked", entitlement, SlugOf(entitlement.FeatureId), cancellationToken);
            await _events.PublishAsync(
                new FeatureEntitlementRevoked(customerId, entitlement.FeatureId, "subscription_no_longer_grants", now),
                cancellationToken);
        }

        // Grant what is newly owed.
        foreach (var (featureId, source) in expected)
        {
            if (held.Any(candidate => candidate.FeatureId == featureId && candidate.IsActiveAt(now)))
            {
                continue;
            }

            var entitlement = FeatureEntitlement.Grant(Guid.NewGuid(), customerId, featureId, source, now);
            await _entitlements.AddAsync(entitlement, cancellationToken);
            await AuditAsync("entitlement.granted", entitlement, SlugOf(featureId), cancellationToken);
            await _events.PublishAsync(
                new FeatureEntitlementGranted(customerId, featureId, source.ToString(), now),
                cancellationToken);
        }

        await _entitlements.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// What the subscription says the customer should hold: everything the plan
    /// includes, plus everything they have chosen that the plan still lets them
    /// choose, minus anything that cannot run on their infrastructure. A
    /// subscription that entitles nothing yields nothing, which is what makes
    /// suspension take effect without a separate code path.
    /// </summary>
    private async Task<Dictionary<Guid, EntitlementSource>> ResolveExpectedAsync(
        Subscription? subscription,
        CancellationToken cancellationToken)
    {
        var expected = new Dictionary<Guid, EntitlementSource>();

        if (subscription is null || !subscription.IsEntitling)
        {
            return expected;
        }

        var plan = await _plans.GetOfferingAsync(subscription.PlanId, cancellationToken);
        if (plan is null)
        {
            return expected;
        }

        foreach (var featureId in plan.IncludedFeatureIds)
        {
            expected[featureId] = EntitlementSource.Plan;
        }

        foreach (var featureId in subscription.EnabledFeatureIds)
        {
            var offering = plan.Find(featureId);

            // The plan may have changed underneath a selection that is no longer
            // on offer. It is then not expected any more, so reconciliation
            // revokes it rather than honouring a choice the plan withdrew.
            if (offering is null || (!offering.IsIncluded && !offering.IsCustomerToggleable))
            {
                continue;
            }

            expected.TryAdd(featureId, offering.IsIncluded ? EntitlementSource.Plan : EntitlementSource.Optional);
        }

        var descriptors = await _features.GetManyAsync(expected.Keys.ToArray(), cancellationToken);

        foreach (var featureId in expected.Keys.ToArray())
        {
            var feature = descriptors.SingleOrDefault(candidate => candidate.FeatureId == featureId);

            if (feature is null ||
                !feature.RemainsEntitled ||
                !await CanRunAsync(subscription.CustomerId, feature, cancellationToken))
            {
                expected.Remove(featureId);
            }
        }

        return expected;
    }

    private async Task<bool> CanRunAsync(Guid customerId, FeatureDescriptor feature, CancellationToken cancellationToken) =>
        !feature.RequiresDedicatedInfrastructure ||
        await _hosting.HasDedicatedCapacityAsync(customerId, cancellationToken);

    private Task AuditAsync(string action, FeatureEntitlement entitlement, string? featureSlug, CancellationToken cancellationToken) =>
        _audit.RecordAsync(
            action,
            nameof(FeatureEntitlement),
            entitlement.Id.ToString(),
            entitlement.CustomerId,
            cancellationToken,
            newValue: new
            {
                featureId = entitlement.FeatureId,
                featureSlug,
                source = entitlement.Source.ToString(),
                entitlement.ExpiresAt,
                entitlement.RevokedReason,
            });

    private static EntitlementView Describe(FeatureEntitlement entitlement, DateTimeOffset now) => new(
        entitlement.FeatureId,
        entitlement.Source.ToString(),
        entitlement.GrantedAt,
        entitlement.ExpiresAt,
        entitlement.IsActiveAt(now));
}
