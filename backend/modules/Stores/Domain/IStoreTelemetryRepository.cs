namespace Stores.Domain;

/// <summary>
/// Persistence for the append-only record of what stores reported: health
/// observations and deployments. Separate from <see cref="IStoreRepository"/>
/// because these are not part of the store aggregate — they are facts about it,
/// they are written far more often than the store itself changes, and they are
/// the first tables a retention job will touch (docs/observability.md).
///
/// Implementations apply the caller's customer scope, so a customer-scoped
/// principal cannot read another customer's telemetry even by guessing a store id.
/// </summary>
public interface IStoreTelemetryRepository
{
    Task AddHealthCheckAsync(StoreHealthCheck healthCheck, CancellationToken cancellationToken);

    Task AddDeploymentAsync(StoreDeployment deployment, CancellationToken cancellationToken);

    Task<IReadOnlyCollection<StoreHealthCheck>> ListHealthChecksAsync(
        Guid storeId,
        int limit,
        CancellationToken cancellationToken);

    Task<IReadOnlyCollection<StoreDeployment>> ListDeploymentsAsync(
        Guid storeId,
        int limit,
        CancellationToken cancellationToken);

    /// <summary>The most recent health observation per store, for the stores listed.</summary>
    Task<IReadOnlyDictionary<Guid, StoreHealthCheck>> LatestHealthChecksAsync(
        IReadOnlyCollection<Guid> storeIds,
        CancellationToken cancellationToken);

    Task SaveChangesAsync(CancellationToken cancellationToken);
}
