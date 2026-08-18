namespace Ordering.Domain;

public sealed record CalculatedModifierSnapshot(
    Guid GroupId,
    string GroupName,
    Guid ModifierId,
    string ModifierName,
    decimal PriceDelta,
    int DisplayOrder);

public sealed record CalculatedItemSnapshot(
    Guid ProductId,
    string ProductName,
    Guid? VariantId,
    string? VariantName,
    decimal UnitBasePrice,
    decimal UnitModifierTotal,
    decimal UnitPrice,
    int Quantity,
    decimal LineTotal,
    int DisplayOrder,
    IReadOnlyList<CalculatedModifierSnapshot> Modifiers);

public sealed record OrderPricingCalculationResult(
    string Currency,
    decimal Subtotal,
    decimal DiscountTotal,
    decimal DiscountedSubtotal,
    decimal FulfillmentFee,
    decimal Total,
    IReadOnlyList<CalculatedItemSnapshot> Items,
    ResolvedOrderFulfillment? ResolvedFulfillment,
    OrderPromotionEvaluationResult? AppliedPromotion = null);

public interface IOrderPricingCalculator
{
    Task<OrderPricingCalculationResult> CalculatePricingAsync(
        Guid tenantId,
        IReadOnlyList<PlaceOrderItemInput> items,
        PlaceOrderFulfillmentInput? fulfillment,
        string? couponCode,
        CancellationToken cancellationToken);
}
