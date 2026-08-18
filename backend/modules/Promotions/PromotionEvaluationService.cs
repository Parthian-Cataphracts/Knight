using Knight.Application.Abstractions.Time;
using Knight.Application.Exceptions;
using Promotions.Domain;

namespace Promotions;

public sealed class PromotionEvaluationService : IPromotionPricingEvaluator
{
    private readonly IPromotionRepository _promotionRepository;
    private readonly IDateTimeProvider _dateTimeProvider;

    public PromotionEvaluationService(
        IPromotionRepository promotionRepository,
        IDateTimeProvider dateTimeProvider)
    {
        _promotionRepository = promotionRepository;
        _dateTimeProvider = dateTimeProvider;
    }

    public async Task<PromotionEvaluationResult> EvaluateAsync(
        Guid tenantId,
        decimal subtotal,
        string? couponCode,
        CancellationToken cancellationToken)
    {
        if (tenantId == Guid.Empty || subtotal <= 0m)
        {
            return PromotionEvaluationResult.None;
        }

        var now = _dateTimeProvider.UtcNow;

        if (!string.IsNullOrWhiteSpace(couponCode))
        {
            var normalizedCode = Coupon.NormalizeCode(couponCode);
            var coupon = await _promotionRepository.GetCouponByNormalizedCodeAsync(tenantId, normalizedCode, cancellationToken);
            if (coupon is null || !coupon.IsActiveAt(now))
            {
                throw new ValidationException(new Dictionary<string, string[]>
                {
                    ["couponCode"] = [$"Coupon '{couponCode.Trim()}' is invalid or expired."]
                });
            }

            if (coupon.UsageLimitTotal.HasValue)
            {
                var usedCount = await _promotionRepository.GetCouponRedemptionCountAsync(tenantId, coupon.Id, cancellationToken);
                if (usedCount >= coupon.UsageLimitTotal.Value)
                {
                    throw new ValidationException(new Dictionary<string, string[]>
                    {
                        ["couponCode"] = [$"Coupon '{couponCode.Trim()}' usage limit has been reached."]
                    });
                }
            }

            var promotion = await _promotionRepository.GetByIdAsync(tenantId, coupon.PromotionId, cancellationToken);
            if (promotion is null || !promotion.IsEligible(subtotal, now))
            {
                throw new ValidationException(new Dictionary<string, string[]>
                {
                    ["couponCode"] = [$"Coupon '{couponCode.Trim()}' is not eligible for this order."]
                });
            }

            var discount = promotion.CalculateDiscount(subtotal, now);
            if (discount <= 0m)
            {
                return PromotionEvaluationResult.None;
            }

            return new PromotionEvaluationResult(new AppliedPromotionResult(
                promotion.Id,
                coupon.Id,
                promotion.Name,
                coupon.Code,
                promotion.DiscountType.ToString(),
                promotion.DiscountValue,
                discount));
        }

        // Automatic promotions evaluation
        var automaticPromotions = await _promotionRepository.GetActiveAutomaticPromotionsAsync(tenantId, now, cancellationToken);
        var candidates = automaticPromotions
            .Where(p => p.IsEligible(subtotal, now))
            .Select(p => new
            {
                Promotion = p,
                Discount = p.CalculateDiscount(subtotal, now)
            })
            .Where(c => c.Discount > 0m)
            .OrderByDescending(c => c.Discount)
            .ThenByDescending(c => c.Promotion.Priority)
            .ThenBy(c => c.Promotion.CreatedAt)
            .ThenBy(c => c.Promotion.Id)
            .ToList();

        if (candidates.Count == 0)
        {
            return PromotionEvaluationResult.None;
        }

        var winner = candidates[0];
        return new PromotionEvaluationResult(new AppliedPromotionResult(
            winner.Promotion.Id,
            null,
            winner.Promotion.Name,
            null,
            winner.Promotion.DiscountType.ToString(),
            winner.Promotion.DiscountValue,
            winner.Discount));
    }
}
