namespace Ordering.Domain;

public interface IOrderPromotionRedeemer
{
    Task RedeemCouponUsageAsync(
        Guid tenantId,
        Guid couponId,
        Guid orderId,
        CancellationToken cancellationToken);
}
