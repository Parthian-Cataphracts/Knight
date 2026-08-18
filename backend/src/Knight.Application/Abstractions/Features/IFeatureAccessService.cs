namespace Knight.Application.Abstractions.Features;

/// <summary>
/// Server-side authority on whether a tenant has access to a capability.
/// This must be the only source of truth consulted before executing
/// feature-gated behavior; frontend menu visibility is not a security control.
/// </summary>
public interface IFeatureAccessService
{
    Task<bool> IsEnabledAsync(Guid tenantId, string featureKey, CancellationToken cancellationToken);

    Task<IReadOnlyCollection<string>> GetEnabledFeatureKeysAsync(Guid tenantId, CancellationToken cancellationToken);
}
