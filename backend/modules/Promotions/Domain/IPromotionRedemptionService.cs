namespace Promotions.Domain;

public interface IPromotionRedemptionService
{
    Task RedeemCouponUsageAsync(
        Guid tenantId,
        Guid couponId,
        Guid orderId,
        CancellationToken cancellationToken);
}
