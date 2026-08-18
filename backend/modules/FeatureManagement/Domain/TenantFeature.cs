using Knight.Domain.Common;
using Knight.Domain.Exceptions;

namespace FeatureManagement.Domain;

/// <summary>
/// Whether a given tenant has a feature enabled. This is a tenant-level toggle
/// only; per-user authorization within an enabled feature is handled separately
/// by permissions (see Identity module).
/// </summary>
public sealed class TenantFeature : Entity, ITenantScoped
{
    public Guid TenantId { get; private set; }

    public string FeatureKey { get; private set; }

    public bool IsEnabled { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    private TenantFeature()
    {
        FeatureKey = string.Empty;
    }

    private TenantFeature(Guid id, Guid tenantId, string featureKey, bool isEnabled, DateTimeOffset updatedAt)
        : base(id)
    {
        TenantId = tenantId;
        FeatureKey = featureKey;
        IsEnabled = isEnabled;
        UpdatedAt = updatedAt;
    }

    public static TenantFeature Create(Guid id, Guid tenantId, string featureKey, bool isEnabled, DateTimeOffset now)
    {
        if (tenantId == Guid.Empty)
        {
            throw DomainException.Validation("A tenant feature must belong to a tenant.");
        }

        if (string.IsNullOrWhiteSpace(featureKey))
        {
            throw DomainException.Validation("Feature key is required.");
        }

        return new TenantFeature(id, tenantId, featureKey.Trim().ToLowerInvariant(), isEnabled, now);
    }

    public void Enable(DateTimeOffset now)
    {
        IsEnabled = true;
        UpdatedAt = now;
    }

    public void Disable(DateTimeOffset now)
    {
        IsEnabled = false;
        UpdatedAt = now;
    }
}
