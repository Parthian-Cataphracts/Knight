namespace Ordering.Domain;

public sealed record OrderPromotionEvaluationResult(
    Guid? SourcePromotionId,
    Guid? SourceCouponId,
    string PromotionName,
    string? CouponCode,
    string DiscountType,
    decimal DiscountValue,
    decimal DiscountAmount);

public interface IOrderPromotionEvaluator
{
    Task<OrderPromotionEvaluationResult?> EvaluateDiscountAsync(
        Guid tenantId,
        decimal subtotal,
        string? couponCode,
        CancellationToken cancellationToken);
}
