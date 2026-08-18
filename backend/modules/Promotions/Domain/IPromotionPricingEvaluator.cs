namespace Promotions.Domain;

public sealed record AppliedPromotionResult(
    Guid PromotionId,
    Guid? CouponId,
    string PromotionName,
    string? CouponCode,
    string DiscountType,
    decimal DiscountValue,
    decimal DiscountAmount);

public sealed record PromotionEvaluationResult(AppliedPromotionResult? AppliedPromotion)
{
    public static readonly PromotionEvaluationResult None = new((AppliedPromotionResult?)null);

    public bool HasDiscount => AppliedPromotion is not null && AppliedPromotion.DiscountAmount > 0m;
    public decimal DiscountTotal => AppliedPromotion?.DiscountAmount ?? 0m;
}

public interface IPromotionPricingEvaluator
{
    Task<PromotionEvaluationResult> EvaluateAsync(
        Guid tenantId,
        decimal subtotal,
        string? couponCode,
        CancellationToken cancellationToken);
}
