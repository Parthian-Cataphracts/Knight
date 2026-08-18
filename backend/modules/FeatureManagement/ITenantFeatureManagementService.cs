namespace FeatureManagement;

/// <summary>
/// Application-facing use cases for toggling tenant feature access. Keeps
/// feature enablement (a platform-authorized administrative action) distinct
/// from <see cref="Knight.Application.Abstractions.Features.IFeatureAccessService"/>,
/// which is the read-only, high-frequency check business code uses.
/// </summary>
public interface ITenantFeatureManagementService
{
    Task EnableAsync(Guid tenantId, string featureKey, CancellationToken cancellationToken);

    Task DisableAsync(Guid tenantId, string featureKey, CancellationToken cancellationToken);
}
