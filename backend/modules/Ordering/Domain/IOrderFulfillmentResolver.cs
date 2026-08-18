namespace Ordering.Domain;

public sealed record ResolvedOrderFulfillment(
    OrderFulfillmentMethod Method,
    decimal Fee,
    Guid? DeliveryZoneId,
    string? DeliveryZoneName,
    string? AddressLine1,
    string? AddressLine2,
    string? City,
    string? PostalCode,
    double? Latitude,
    double? Longitude);

public sealed record PlaceOrderAddressInput(
    string? AddressLine1,
    string? AddressLine2,
    string? City,
    string? PostalCode,
    double? Latitude,
    double? Longitude);

public sealed record PlaceOrderFulfillmentInput(
    OrderFulfillmentMethod Method,
    Guid? DeliveryZoneId = null,
    PlaceOrderAddressInput? Address = null);

public interface IOrderFulfillmentResolver
{
    Task<ResolvedOrderFulfillment?> ResolveFulfillmentAsync(
        Guid tenantId,
        decimal orderSubtotal,
        PlaceOrderFulfillmentInput? input,
        CancellationToken cancellationToken = default);
}
