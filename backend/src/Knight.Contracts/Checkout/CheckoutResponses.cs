namespace Knight.Contracts.Checkout;

public sealed record CheckoutQuoteItemModifierResponse(
    Guid ModifierId,
    string ModifierName,
    decimal Price);

public sealed record CheckoutQuoteItemResponse(
    Guid ProductId,
    string ProductName,
    Guid? VariantId,
    string? VariantName,
    int Quantity,
    decimal UnitBasePrice,
    decimal UnitModifierTotal,
    decimal UnitPrice,
    decimal LineTotal,
    IReadOnlyList<CheckoutQuoteItemModifierResponse> Modifiers);

public sealed record AppliedPromotionQuoteResponse(
    string Name,
    string DiscountType,
    decimal DiscountValue,
    decimal DiscountAmount,
    string? CouponCode = null);

public sealed record CheckoutQuoteResponse(
    string Currency,
    decimal Subtotal,
    decimal DiscountTotal,
    decimal DiscountedSubtotal,
    decimal FulfillmentFee,
    decimal Total,
    IReadOnlyList<CheckoutQuoteItemResponse> Items,
    AppliedPromotionQuoteResponse? AppliedPromotion = null);

public sealed record CheckoutSubmitResponse(
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
