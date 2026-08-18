namespace FeatureRegistry.Domain;

/// <summary>
/// Persistence contract for feature identities. Features are platform-owned:
/// the catalogue is the same for every customer, so nothing here is customer
/// filtered. What differs per customer is which of them they are entitled to.
/// </summary>
public interface IFeatureRepository
{
    Task<Feature?> GetByIdAsync(Guid id, CancellationToken cancellationToken);

    Task<Feature?> GetBySlugAsync(string normalizedSlug, CancellationToken cancellationToken);

    Task<IReadOnlyCollection<Feature>> GetManyAsync(IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken);

    Task<(IReadOnlyCollection<Feature> Items, long TotalCount)> ListAsync(
        int page,
        int pageSize,
        FeatureStatus? status,
        string? category,
        string? search,
        CancellationToken cancellationToken);

    Task AddAsync(Feature feature, CancellationToken cancellationToken);

    Task SaveChangesAsync(CancellationToken cancellationToken);
}
