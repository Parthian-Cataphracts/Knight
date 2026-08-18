using Delivery.Domain;
using Fulfillment.Domain;
using Knight.Contracts.Delivery;
using Knight.Contracts.Fulfillment;

namespace Knight.Api.Endpoints;

internal static class DeliveryEndpointSupport
{
    internal static TenantFulfillmentSettingsResponse ToResponse(TenantFulfillmentSettings settings) =>
        new()
        {
            TenantId = settings.TenantId,
            PickupEnabled = settings.PickupEnabled,
            CreatedAt = settings.CreatedAt,
            UpdatedAt = settings.UpdatedAt
        };

    internal static TenantDeliverySettingsResponse ToResponse(TenantDeliverySettings settings) =>
        new()
        {
            TenantId = settings.TenantId,
            IsAcceptingDeliveryOrders = settings.IsAcceptingDeliveryOrders,
            DefaultMinimumOrderSubtotal = settings.DefaultMinimumOrderSubtotal,
            CreatedAt = settings.CreatedAt,
            UpdatedAt = settings.UpdatedAt
        };

    internal static DeliveryZoneResponse ToResponse(DeliveryZone zone) =>
        new()
        {
            Id = zone.Id,
            Name = zone.Name,
            Fee = zone.Fee,
            MinimumOrderSubtotal = zone.MinimumOrderSubtotal,
            Status = zone.Status.ToString(),
            DisplayOrder = zone.DisplayOrder,
            CreatedAt = zone.CreatedAt,
            UpdatedAt = zone.UpdatedAt,
            ArchivedAt = zone.ArchivedAt
        };
}
