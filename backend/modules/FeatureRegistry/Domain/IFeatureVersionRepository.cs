namespace FeatureRegistry.Domain;

/// <summary>
/// Persistence for feature versions. Like features themselves these are
/// platform-owned: the catalogue is the same for every customer, so nothing here
/// is customer filtered. What differs per customer is which of them they are
/// entitled to and which of them their stores have installed.
/// </summary>
public interface IFeatureVersionRepository
{
    Task<FeatureVersion?> GetByIdAsync(Guid id, CancellationToken cancellationToken);

    Task<FeatureVersion?> FindAsync(Guid featureId, string version, CancellationToken cancellationToken);

    Task<IReadOnlyCollection<FeatureVersion>> ListForFeatureAsync(Guid featureId, CancellationToken cancellationToken);

    /// <summary>
    /// Every feature and version the resolver needs, in one read.
    ///
    /// The whole catalogue is loaded rather than the transitive closure of one
    /// request. The registry is platform-wide and small — tens of features, not
    /// millions of rows — and resolving a diamond needs versions the caller
    /// cannot know it needs until it has already chosen one. Walking the graph
    /// with a query per level would be many round trips to avoid reading a table
    /// that fits comfortably in memory.
    /// </summary>
    Task<IReadOnlyCollection<RegistryFeature>> GetRegistrySnapshotAsync(CancellationToken cancellationToken);

    /// <summary>Everything a given signing key ever signed — the query a key compromise is contained with.</summary>
    Task<IReadOnlyCollection<FeatureVersion>> ListBySigningKeyAsync(string signingKeyId, CancellationToken cancellationToken);

    Task AddAsync(FeatureVersion version, CancellationToken cancellationToken);

    Task SaveChangesAsync(CancellationToken cancellationToken);
}
