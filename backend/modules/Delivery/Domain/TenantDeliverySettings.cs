using Knight.Domain.Common;
using Knight.Domain.Exceptions;

namespace Delivery.Domain;

public sealed class TenantDeliverySettings : ITenantScoped
{
    public Guid TenantId { get; private set; }
    public bool IsAcceptingDeliveryOrders { get; private set; }
    public decimal? DefaultMinimumOrderSubtotal { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset? UpdatedAt { get; private set; }

    private TenantDeliverySettings()
    {
    }

    private TenantDeliverySettings(
        Guid tenantId,
        bool isAcceptingDeliveryOrders,
        decimal? defaultMinimumOrderSubtotal,
        DateTimeOffset createdAt,
        DateTimeOffset? updatedAt)
    {
        TenantId = tenantId;
        IsAcceptingDeliveryOrders = isAcceptingDeliveryOrders;
        DefaultMinimumOrderSubtotal = defaultMinimumOrderSubtotal;
        CreatedAt = createdAt;
        UpdatedAt = updatedAt;
    }

    public static TenantDeliverySettings Create(
        Guid tenantId,
        DateTimeOffset now,
        bool isAcceptingDeliveryOrders = true,
        decimal? defaultMinimumOrderSubtotal = null)
    {
        if (tenantId == Guid.Empty)
        {
            throw DomainException.Validation("Tenant ID is required for delivery settings.");
        }

        if (defaultMinimumOrderSubtotal.HasValue && defaultMinimumOrderSubtotal.Value < 0)
        {
            throw DomainException.Validation("Default minimum order subtotal cannot be negative.");
        }

        return new TenantDeliverySettings(
            tenantId,
            isAcceptingDeliveryOrders,
            defaultMinimumOrderSubtotal,
            now,
            null);
    }

    public void Update(
        bool isAcceptingDeliveryOrders,
        decimal? defaultMinimumOrderSubtotal,
        DateTimeOffset now)
    {
        if (defaultMinimumOrderSubtotal.HasValue && defaultMinimumOrderSubtotal.Value < 0)
        {
            throw DomainException.Validation("Default minimum order subtotal cannot be negative.");
        }

        IsAcceptingDeliveryOrders = isAcceptingDeliveryOrders;
        DefaultMinimumOrderSubtotal = defaultMinimumOrderSubtotal;
        UpdatedAt = now;
    }
}
