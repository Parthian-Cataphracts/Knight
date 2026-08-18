namespace Stores.Domain;

/// <summary>
/// Persistence contract for stores. Implementations apply the caller's customer
/// scope, so a customer-scoped principal cannot reach another customer's store
/// even by guessing an id (docs/authorization.md section 4).
/// </summary>
public interface IStoreRepository
{
    /// <summary>Loads the store together with its credentials, which are only ever mutated through the aggregate.</summary>
    Task<Store?> GetByIdAsync(Guid id, CancellationToken cancellationToken);

    Task<Store?> GetBySlugAsync(string normalizedSlug, CancellationToken cancellationToken);

    Task<Store?> GetByPrimaryDomainAsync(string normalizedHost, CancellationToken cancellationToken);

    /// <summary>Resolves a store from a credential's client id; used by the ingestion handshake.</summary>
    Task<Store?> GetByClientIdAsync(string clientId, CancellationToken cancellationToken);

    Task<(IReadOnlyCollection<Store> Items, long TotalCount)> ListAsync(
        int page,
        int pageSize,
        Guid? customerId,
        StoreEnvironment? environment,
        StoreStatus? status,
        CancellationToken cancellationToken);

    Task AddAsync(Store store, CancellationToken cancellationToken);

    /// <summary>
    /// Registers a credential created through the aggregate as an insert. Required
    /// in addition to mutating the collection: EF Core does not reliably classify a
    /// new child discovered only by traversing a tracked parent's navigation.
    /// </summary>
    void RegisterNewCredential(StoreCredential credential);

    Task SaveChangesAsync(CancellationToken cancellationToken);
}
