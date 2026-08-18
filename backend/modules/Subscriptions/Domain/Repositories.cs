namespace Subscriptions.Domain;

/// <summary>
/// Persistence for subscriptions. Customer-scoped: a customer-scoped principal
/// sees their own subscription and no other, enforced by the persistence filter
/// rather than by each caller remembering.
/// </summary>
public interface ISubscriptionRepository
{
    Task<Subscription?> GetByIdAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>The customer's current subscription, cancelled ones excluded.</summary>
    Task<Subscription?> GetActiveForCustomerAsync(Guid customerId, CancellationToken cancellationToken);

    Task<(IReadOnlyCollection<Subscription> Items, long TotalCount)> ListAsync(
        int page,
        int pageSize,
        Guid? customerId,
        SubscriptionStatus? status,
        CancellationToken cancellationToken);

    Task AddAsync(Subscription subscription, CancellationToken cancellationToken);

    void RegisterNewFeature(SubscriptionFeature feature);

    void RemoveFeature(SubscriptionFeature feature);

    Task SaveChangesAsync(CancellationToken cancellationToken);
}

public interface IFeatureEntitlementRepository
{
    Task<IReadOnlyCollection<FeatureEntitlement>> ListForCustomerAsync(
        Guid customerId,
        bool includeInactive,
        CancellationToken cancellationToken);

    Task<FeatureEntitlement?> FindActiveAsync(
        Guid customerId,
        Guid featureId,
        DateTimeOffset moment,
        CancellationToken cancellationToken);

    Task AddAsync(FeatureEntitlement entitlement, CancellationToken cancellationToken);

    Task SaveChangesAsync(CancellationToken cancellationToken);
}
