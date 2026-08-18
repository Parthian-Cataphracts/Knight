using Delivery;
using Delivery.Domain;
using Fulfillment.Domain;
using Ordering.Domain;
using Knight.Application.Abstractions.Features;
using Knight.Application.Exceptions;

namespace Knight.Infrastructure.Adapters;

public sealed class OrderFulfillmentResolver : IOrderFulfillmentResolver
{
    private readonly IFeatureAccessService _featureAccess;
    private readonly ITenantFulfillmentSettingsRepository _fulfillmentSettingsRepository;
    private readonly ITenantDeliverySettingsRepository _deliverySettingsRepository;
    private readonly IDeliveryZoneRepository _zoneRepository;

    public OrderFulfillmentResolver(
        IFeatureAccessService featureAccess,
        ITenantFulfillmentSettingsRepository fulfillmentSettingsRepository,
        ITenantDeliverySettingsRepository deliverySettingsRepository,
        IDeliveryZoneRepository zoneRepository)
    {
        _featureAccess = featureAccess;
        _fulfillmentSettingsRepository = fulfillmentSettingsRepository;
        _deliverySettingsRepository = deliverySettingsRepository;
        _zoneRepository = zoneRepository;
    }

    public async Task<ResolvedOrderFulfillment?> ResolveFulfillmentAsync(
        Guid tenantId,
        decimal orderSubtotal,
        PlaceOrderFulfillmentInput? input,
        CancellationToken cancellationToken = default)
    {
        if (input is null)
        {
            return null;
        }

        if (input.Method == OrderFulfillmentMethod.Pickup)
        {
            var fulfillmentSettings = await _fulfillmentSettingsRepository.GetByTenantIdAsync(tenantId, cancellationToken);
            if (fulfillmentSettings is not null && !fulfillmentSettings.PickupEnabled)
            {
                throw new ValidationException(new Dictionary<string, string[]>
                {
                    ["fulfillment"] = ["Pickup is currently disabled for this tenant."]
                });
            }

            return new ResolvedOrderFulfillment(
                OrderFulfillmentMethod.Pickup,
                Fee: 0m,
                DeliveryZoneId: null,
                DeliveryZoneName: null,
                AddressLine1: null,
                AddressLine2: null,
                City: null,
                PostalCode: null,
                Latitude: null,
                Longitude: null);
        }

        if (input.Method == OrderFulfillmentMethod.Delivery)
        {
            var isDeliveryEnabled = await _featureAccess.IsEnabledAsync(tenantId, DeliveryFeature.Key, cancellationToken);
            if (!isDeliveryEnabled)
            {
                throw new ValidationException(new Dictionary<string, string[]>
                {
                    ["fulfillment"] = ["Delivery feature is not enabled for this tenant."]
                });
            }

            var deliverySettings = await _deliverySettingsRepository.GetByTenantIdAsync(tenantId, cancellationToken);
            if (deliverySettings is not null && !deliverySettings.IsAcceptingDeliveryOrders)
            {
                throw new ValidationException(new Dictionary<string, string[]>
                {
                    ["fulfillment"] = ["Delivery orders are currently not being accepted."]
                });
            }

            if (!input.DeliveryZoneId.HasValue)
            {
                throw new ValidationException(new Dictionary<string, string[]>
                {
                    ["deliveryZoneId"] = ["DeliveryZoneId is required for delivery fulfillment."]
                });
            }

            var zone = await _zoneRepository.GetByIdAsync(tenantId, input.DeliveryZoneId.Value, cancellationToken);
            if (zone is null)
            {
                throw new ValidationException(new Dictionary<string, string[]>
                {
                    ["deliveryZoneId"] = [$"Delivery zone '{input.DeliveryZoneId.Value}' was not found for this tenant."]
                });
            }

            if (zone.Status != DeliveryZoneStatus.Active)
            {
                throw new ValidationException(new Dictionary<string, string[]>
                {
                    ["deliveryZoneId"] = [$"Delivery zone '{zone.Name}' is not active."]
                });
            }

            var effectiveMinimum = zone.MinimumOrderSubtotal ?? deliverySettings?.DefaultMinimumOrderSubtotal;
            if (effectiveMinimum.HasValue && orderSubtotal < effectiveMinimum.Value)
            {
                throw new ValidationException(new Dictionary<string, string[]>
                {
                    ["subtotal"] = [$"Order subtotal {orderSubtotal:F2} is below the minimum required delivery subtotal of {effectiveMinimum.Value:F2}."]
                });
            }

            if (input.Address is null)
            {
                throw new ValidationException(new Dictionary<string, string[]>
                {
                    ["address"] = ["Delivery address is required."]
                });
            }

            if (string.IsNullOrWhiteSpace(input.Address.AddressLine1))
            {
                throw new ValidationException(new Dictionary<string, string[]>
                {
                    ["address.addressLine1"] = ["Address line 1 is required for delivery."]
                });
            }

            if (string.IsNullOrWhiteSpace(input.Address.City))
            {
                throw new ValidationException(new Dictionary<string, string[]>
                {
                    ["address.city"] = ["City is required for delivery."]
                });
            }

            if (input.Address.Latitude.HasValue != input.Address.Longitude.HasValue)
            {
                throw new ValidationException(new Dictionary<string, string[]>
                {
                    ["address.coordinates"] = ["Both latitude and longitude must be provided together."]
                });
            }

            if (input.Address.Latitude.HasValue && (input.Address.Latitude.Value < -90 || input.Address.Latitude.Value > 90))
            {
                throw new ValidationException(new Dictionary<string, string[]>
                {
                    ["address.latitude"] = ["Latitude must be between -90 and 90 degrees."]
                });
            }

            if (input.Address.Longitude.HasValue && (input.Address.Longitude.Value < -180 || input.Address.Longitude.Value > 180))
            {
                throw new ValidationException(new Dictionary<string, string[]>
                {
                    ["address.longitude"] = ["Longitude must be between -180 and 180 degrees."]
                });
            }

            return new ResolvedOrderFulfillment(
                OrderFulfillmentMethod.Delivery,
                Fee: zone.Fee,
                DeliveryZoneId: zone.Id,
                DeliveryZoneName: zone.Name,
                AddressLine1: input.Address.AddressLine1.Trim(),
                AddressLine2: string.IsNullOrWhiteSpace(input.Address.AddressLine2) ? null : input.Address.AddressLine2.Trim(),
                City: input.Address.City.Trim(),
                PostalCode: string.IsNullOrWhiteSpace(input.Address.PostalCode) ? null : input.Address.PostalCode.Trim(),
                Latitude: input.Address.Latitude,
                Longitude: input.Address.Longitude);
        }

        throw new ValidationException(new Dictionary<string, string[]>
        {
            ["fulfillment.method"] = ["Invalid fulfillment method."]
        });
    }
}
