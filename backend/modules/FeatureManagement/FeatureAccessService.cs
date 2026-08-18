using FeatureManagement.Domain;
using Knight.Application.Abstractions.Features;

namespace FeatureManagement;

/// <summary>
/// Server-side authority for tenant feature access. This is the only implementation
/// of <see cref="IFeatureAccessService"/> — never bypass it with client-side checks.
/// </summary>
public sealed class FeatureAccessService : IFeatureAccessService
{
    private readonly IFeatureStore _featureStore;

    public FeatureAccessService(IFeatureStore featureStore)
    {
        _featureStore = featureStore;
    }

    public async Task<bool> IsEnabledAsync(Guid tenantId, string featureKey, CancellationToken cancellationToken)
    {
        var tenantFeature = await _featureStore.GetTenantFeatureAsync(tenantId, featureKey, cancellationToken);
        return tenantFeature is { IsEnabled: true };
    }

    public async Task<IReadOnlyCollection<string>> GetEnabledFeatureKeysAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        var enabled = await _featureStore.GetEnabledTenantFeaturesAsync(tenantId, cancellationToken);
        return enabled.Select(f => f.FeatureKey).ToArray();
    }
}
