using FeatureRegistry.Domain;

namespace FeatureRegistry;

public sealed record CreateFeatureInput(
    string Slug,
    string Name,
    string? Description,
    string Category,
    bool IsOptional,
    bool RequiresDedicatedInfrastructure);

public sealed record UpdateFeatureInput(string Name, string? Description, string Category);

public sealed record FeatureListQuery(int Page, int PageSize, FeatureStatus? Status, string? Category, string? Search);

public sealed record FeaturePage(IReadOnlyCollection<Feature> Items, int Page, int PageSize, long TotalCount);

/// <summary>
/// The feature catalogue: identities and commercial metadata only. Versions,
/// manifests, signed artifacts, dependencies and installation are the registry
/// and delivery subsystem, which arrives in phase 3.5 — publishing a Feature
/// *identity* here says the capability may be sold, not that any code exists to
/// ship (docs/feature-delivery.md).
/// </summary>
public interface IFeatureCatalogService
{
    Task<FeaturePage> ListAsync(FeatureListQuery query, CancellationToken cancellationToken);

    Task<Feature?> GetAsync(Guid id, CancellationToken cancellationToken);

    Task<Feature> CreateAsync(CreateFeatureInput input, CancellationToken cancellationToken);

    Task<Feature> UpdateAsync(Guid id, UpdateFeatureInput input, CancellationToken cancellationToken);

    Task<Feature> PublishAsync(Guid id, CancellationToken cancellationToken);

    Task<Feature> DeprecateAsync(Guid id, CancellationToken cancellationToken);

    Task<Feature> WithdrawAsync(Guid id, CancellationToken cancellationToken);
}
