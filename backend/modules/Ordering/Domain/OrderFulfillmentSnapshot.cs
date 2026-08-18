using Knight.Domain.Common;
using Knight.Domain.Exceptions;

namespace Ordering.Domain;

public enum OrderFulfillmentMethod
{
    Pickup = 1,
    Delivery = 2
}

public sealed class OrderFulfillmentSnapshot : ITenantScoped
{
    private const int MaxNameLength = 100;
    private const int MaxAddressLength = 200;
    private const int MaxCityLength = 100;
    private const int MaxPostalCodeLength = 50;

    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }
    public Guid OrderId { get; private set; }
    public OrderFulfillmentMethod Method { get; private set; }
    public decimal FulfillmentFee { get; private set; }
    public Guid? DeliveryZoneId { get; private set; }
    public string? DeliveryZoneName { get; private set; }
    public string? AddressLine1 { get; private set; }
    public string? AddressLine2 { get; private set; }
    public string? City { get; private set; }
    public string? PostalCode { get; private set; }
    public double? Latitude { get; private set; }
    public double? Longitude { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    private OrderFulfillmentSnapshot()
    {
    }

    private OrderFulfillmentSnapshot(
        Guid id,
        DateTimeOffset createdAt,
        Guid tenantId,
        Guid orderId,
        OrderFulfillmentMethod method,
        decimal fulfillmentFee,
        Guid? deliveryZoneId,
        string? deliveryZoneName,
        string? addressLine1,
        string? addressLine2,
        string? city,
        string? postalCode,
        double? latitude,
        double? longitude)
    {
        Id = id;
        CreatedAt = createdAt;
        TenantId = tenantId;
        OrderId = orderId;
        Method = method;
        FulfillmentFee = fulfillmentFee;
        DeliveryZoneId = deliveryZoneId;
        DeliveryZoneName = deliveryZoneName;
        AddressLine1 = addressLine1;
        AddressLine2 = addressLine2;
        City = city;
        PostalCode = postalCode;
        Latitude = latitude;
        Longitude = longitude;
    }

    public static OrderFulfillmentSnapshot CreatePickup(
        Guid id,
        DateTimeOffset now,
        Guid tenantId,
        Guid orderId)
    {
        if (id == Guid.Empty) throw DomainException.Validation("Fulfillment snapshot ID is required.");
        if (tenantId == Guid.Empty) throw DomainException.Validation("Tenant ID is required.");
        if (orderId == Guid.Empty) throw DomainException.Validation("Order ID is required.");

        return new OrderFulfillmentSnapshot(
            id,
            now,
            tenantId,
            orderId,
            OrderFulfillmentMethod.Pickup,
            fulfillmentFee: 0m,
            deliveryZoneId: null,
            deliveryZoneName: null,
            addressLine1: null,
            addressLine2: null,
            city: null,
            postalCode: null,
            latitude: null,
            longitude: null);
    }

    public static OrderFulfillmentSnapshot CreateDelivery(
        Guid id,
        DateTimeOffset now,
        Guid tenantId,
        Guid orderId,
        Guid? deliveryZoneId,
        string? deliveryZoneName,
        decimal fee,
        string? addressLine1,
        string? addressLine2,
        string? city,
        string? postalCode,
        double? latitude,
        double? longitude)
    {
        if (id == Guid.Empty) throw DomainException.Validation("Fulfillment snapshot ID is required.");
        if (tenantId == Guid.Empty) throw DomainException.Validation("Tenant ID is required.");
        if (orderId == Guid.Empty) throw DomainException.Validation("Order ID is required.");

        if (fee < 0)
        {
            throw DomainException.Validation("Delivery fee cannot be negative.");
        }

        if (string.IsNullOrWhiteSpace(addressLine1))
        {
            throw DomainException.Validation("Address line 1 is required for delivery.");
        }

        if (string.IsNullOrWhiteSpace(city))
        {
            throw DomainException.Validation("City is required for delivery.");
        }

        var trimmedLine1 = addressLine1.Trim();
        if (trimmedLine1.Length > MaxAddressLength)
        {
            throw DomainException.Validation($"Address line 1 cannot exceed {MaxAddressLength} characters.");
        }

        var trimmedLine2 = string.IsNullOrWhiteSpace(addressLine2) ? null : addressLine2.Trim();
        if (trimmedLine2 is not null && trimmedLine2.Length > MaxAddressLength)
        {
            throw DomainException.Validation($"Address line 2 cannot exceed {MaxAddressLength} characters.");
        }

        var trimmedCity = city.Trim();
        if (trimmedCity.Length > MaxCityLength)
        {
            throw DomainException.Validation($"City cannot exceed {MaxCityLength} characters.");
        }

        var trimmedPostal = string.IsNullOrWhiteSpace(postalCode) ? null : postalCode.Trim();
        if (trimmedPostal is not null && trimmedPostal.Length > MaxPostalCodeLength)
        {
            throw DomainException.Validation($"Postal code cannot exceed {MaxPostalCodeLength} characters.");
        }

        var trimmedZoneName = string.IsNullOrWhiteSpace(deliveryZoneName) ? null : deliveryZoneName.Trim();
        if (trimmedZoneName is not null && trimmedZoneName.Length > MaxNameLength)
        {
            throw DomainException.Validation($"Zone name cannot exceed {MaxNameLength} characters.");
        }

        if (latitude.HasValue != longitude.HasValue)
        {
            throw DomainException.Validation("Both latitude and longitude must be provided together.");
        }

        if (latitude.HasValue && (latitude.Value < -90 || latitude.Value > 90))
        {
            throw DomainException.Validation("Latitude must be between -90 and 90 degrees.");
        }

        if (longitude.HasValue && (longitude.Value < -180 || longitude.Value > 180))
        {
            throw DomainException.Validation("Longitude must be between -180 and 180 degrees.");
        }

        return new OrderFulfillmentSnapshot(
            id,
            now,
            tenantId,
            orderId,
            OrderFulfillmentMethod.Delivery,
            fee,
            deliveryZoneId,
            trimmedZoneName,
            trimmedLine1,
            trimmedLine2,
            trimmedCity,
            trimmedPostal,
            latitude,
            longitude);
    }
}
