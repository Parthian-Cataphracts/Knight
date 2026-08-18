namespace Checkout.Domain;

public sealed record CheckoutItemSelection(
    Guid ProductId,
    Guid? VariantId,
    int Quantity,
    IReadOnlyList<Guid>? ModifierIds);

public sealed record CheckoutFulfillmentSelection(
    string Method,
    Guid? DeliveryZoneId,
    string? AddressLine1,
    string? AddressLine2,
    string? City,
    string? PostalCode,
    decimal? Latitude,
    decimal? Longitude);

public sealed record CheckoutGuestPartySelection(
    string DisplayName,
    string? Phone,
    string? Email);

public sealed record CheckoutQuoteModifierResult(
    Guid ModifierId,
    string ModifierName,
    decimal Price);

public sealed record CheckoutQuoteItemResult(
    Guid ProductId,
    string ProductName,
    Guid? VariantId,
    string? VariantName,
    int Quantity,
    decimal UnitBasePrice,
    decimal UnitModifierTotal,
    decimal UnitPrice,
    decimal LineTotal,
    IReadOnlyList<CheckoutQuoteModifierResult> Modifiers);

public sealed record AppliedPromotionQuoteResult(
    string Name,
    string DiscountType,
    decimal DiscountValue,
    decimal DiscountAmount,
    string? CouponCode = null);

public sealed record CheckoutQuoteResult(
    string Currency,
    decimal Subtotal,
    decimal DiscountTotal,
    decimal DiscountedSubtotal,
    decimal FulfillmentFee,
    decimal Total,
    IReadOnlyList<CheckoutQuoteItemResult> Items,
    AppliedPromotionQuoteResult? AppliedPromotion = null);

public sealed record CheckoutOrderPlacementResult(
    Guid OrderId,
    long OrderNumber,
    string Status,
    string Currency,
    decimal Subtotal,
    decimal DiscountTotal,
    decimal DiscountedSubtotal,
    decimal FulfillmentFee,
    decimal Total,
    string FulfillmentMethod,
    DateTimeOffset CreatedAt,
    string? PromotionName = null,
    string? CouponCode = null);

public interface ICheckoutOrderingGateway
{
    Task<CheckoutQuoteResult> CalculateQuoteAsync(
        Guid tenantId,
        IReadOnlyList<CheckoutItemSelection> items,
        CheckoutFulfillmentSelection? fulfillment,
        string? couponCode,
        CancellationToken cancellationToken);

    Task<CheckoutOrderPlacementResult> PlaceOrderAsync(
        Guid tenantId,
        CheckoutGuestPartySelection guestParty,
        IReadOnlyList<CheckoutItemSelection> items,
        CheckoutFulfillmentSelection fulfillment,
        string? couponCode,
        CancellationToken cancellationToken);

    Task<CheckoutOrderPlacementResult?> GetOrderReplayAsync(
        Guid tenantId,
        Guid orderId,
        CancellationToken cancellationToken);
}
