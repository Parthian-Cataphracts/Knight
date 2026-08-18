using Knight.Domain.Common;
using Knight.Domain.Exceptions;

namespace Fulfillment.Domain;

public sealed class TenantFulfillmentSettings : ITenantScoped
{
    public Guid TenantId { get; private set; }
    public bool PickupEnabled { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset? UpdatedAt { get; private set; }

    private TenantFulfillmentSettings()
    {
    }

    private TenantFulfillmentSettings(
        Guid tenantId,
        bool pickupEnabled,
        DateTimeOffset createdAt,
        DateTimeOffset? updatedAt)
    {
        TenantId = tenantId;
        PickupEnabled = pickupEnabled;
        CreatedAt = createdAt;
        UpdatedAt = updatedAt;
    }

    public static TenantFulfillmentSettings Create(
        Guid tenantId,
        DateTimeOffset now,
        bool pickupEnabled = true)
    {
        if (tenantId == Guid.Empty)
        {
            throw DomainException.Validation("Tenant ID is required for fulfillment settings.");
        }

        return new TenantFulfillmentSettings(tenantId, pickupEnabled, now, null);
    }

    public void Update(bool pickupEnabled, DateTimeOffset now)
    {
        PickupEnabled = pickupEnabled;
        UpdatedAt = now;
    }
}
