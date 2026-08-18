namespace FeatureManagement.Domain;

/// <summary>
/// Persistence contract for feature definitions and tenant feature toggles.
/// Implemented in Infrastructure.
/// </summary>
public interface IFeatureStore
{
    Task<FeatureDefinition?> GetDefinitionAsync(string featureKey, CancellationToken cancellationToken);

    Task<IReadOnlyCollection<FeatureDefinition>> GetAllDefinitionsAsync(CancellationToken cancellationToken);

    Task<TenantFeature?> GetTenantFeatureAsync(Guid tenantId, string featureKey, CancellationToken cancellationToken);

    Task<IReadOnlyCollection<TenantFeature>> GetEnabledTenantFeaturesAsync(Guid tenantId, CancellationToken cancellationToken);

    /// <summary>
    /// Enables or disables a tenant's feature, creating the toggle row on first use.
    /// The feature definition must already exist — callers are responsible for
    /// validating that before calling this.
    /// </summary>
    Task SetTenantFeatureAsync(Guid tenantId, string featureKey, bool isEnabled, DateTimeOffset now, CancellationToken cancellationToken);
}
