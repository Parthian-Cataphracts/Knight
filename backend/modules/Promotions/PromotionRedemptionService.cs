using Knight.Application.Abstractions.Time;
using Knight.Application.Exceptions;
using Promotions.Domain;

namespace Promotions;

public sealed class PromotionRedemptionService : IPromotionRedemptionService
{
    private readonly IPromotionRepository _promotionRepository;
    private readonly IDateTimeProvider _dateTimeProvider;

    public PromotionRedemptionService(
        IPromotionRepository promotionRepository,
        IDateTimeProvider dateTimeProvider)
    {
        _promotionRepository = promotionRepository;
        _dateTimeProvider = dateTimeProvider;
    }

    public async Task RedeemCouponUsageAsync(
        Guid tenantId,
        Guid couponId,
        Guid orderId,
        CancellationToken cancellationToken)
    {
        if (tenantId == Guid.Empty) throw new ArgumentException("Tenant ID cannot be empty.", nameof(tenantId));
        if (couponId == Guid.Empty) throw new ArgumentException("Coupon ID cannot be empty.", nameof(couponId));
        if (orderId == Guid.Empty) throw new ArgumentException("Order ID cannot be empty.", nameof(orderId));

        var now = _dateTimeProvider.UtcNow;

        // Acquire row-level lock on coupon to serialize concurrent redemptions against PostgreSQL
        var coupon = await _promotionRepository.GetCouponByIdForUpdateAsync(tenantId, couponId, cancellationToken);
        if (coupon is null || !coupon.IsActiveAt(now))
        {
            throw new ValidationException(new Dictionary<string, string[]>
            {
                ["couponCode"] = ["Coupon is invalid or inactive."]
            });
        }

        // Idempotency check: if order has already redeemed this coupon, do not double-count
        var alreadyRedeemed = await _promotionRepository.HasOrderRedeemedCouponAsync(tenantId, couponId, orderId, cancellationToken);
        if (alreadyRedeemed)
        {
            return;
        }

        if (coupon.UsageLimitTotal.HasValue)
        {
            var usedCount = await _promotionRepository.GetCouponRedemptionCountAsync(tenantId, couponId, cancellationToken);
            if (usedCount >= coupon.UsageLimitTotal.Value)
            {
                throw new ConflictException("Coupon usage limit has been reached.");
            }
        }

        var redemption = CouponRedemption.Create(
            Guid.NewGuid(),
            tenantId,
            couponId,
            orderId,
            now);

        await _promotionRepository.AddCouponRedemptionAsync(redemption, cancellationToken);
    }
}
