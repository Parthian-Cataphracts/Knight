namespace Knight.Contracts.Checkout;

public sealed record CheckoutItemRequest(
    Guid ProductId,
    Guid? VariantId,
    int Quantity,
    IReadOnlyList<Guid>? ModifierIds);

public sealed record CheckoutFulfillmentRequest(
    string Method,
    Guid? DeliveryZoneId,
    string? AddressLine1,
    string? AddressLine2,
    string? City,
    string? PostalCode,
    decimal? Latitude,
    decimal? Longitude);

public sealed record CheckoutGuestPartyRequest(
    string DisplayName,
    string? Phone,
    string? Email);

public sealed record CheckoutQuoteRequest(
    IReadOnlyList<CheckoutItemRequest> Items,
    CheckoutFulfillmentRequest? Fulfillment,
    string? CouponCode = null);

public sealed record CheckoutSubmitRequest(
    CheckoutGuestPartyRequest GuestParty,
    IReadOnlyList<CheckoutItemRequest> Items,
    CheckoutFulfillmentRequest Fulfillment,
    string? CouponCode = null);
