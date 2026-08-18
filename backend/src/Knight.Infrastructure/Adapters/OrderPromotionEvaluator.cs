using Ordering.Domain;
using Promotions.Domain;

namespace Knight.Infrastructure.Adapters;

public sealed class OrderPromotionEvaluator : IOrderPromotionEvaluator
{
    private readonly IPromotionPricingEvaluator _evaluator;

    public OrderPromotionEvaluator(IPromotionPricingEvaluator evaluator)
    {
        _evaluator = evaluator;
    }

    public async Task<OrderPromotionEvaluationResult?> EvaluateDiscountAsync(
        Guid tenantId,
        decimal subtotal,
        string? couponCode,
        CancellationToken cancellationToken)
    {
        var result = await _evaluator.EvaluateAsync(tenantId, subtotal, couponCode, cancellationToken);
        if (!result.HasDiscount || result.AppliedPromotion is null)
        {
            return null;
        }

        var ap = result.AppliedPromotion;
        return new OrderPromotionEvaluationResult(
            ap.PromotionId,
            ap.CouponId,
            ap.PromotionName,
            ap.CouponCode,
            ap.DiscountType,
            ap.DiscountValue,
            ap.DiscountAmount);
    }
}
