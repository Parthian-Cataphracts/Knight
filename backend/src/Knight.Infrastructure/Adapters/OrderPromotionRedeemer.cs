using Ordering.Domain;
using Promotions.Domain;

namespace Knight.Infrastructure.Adapters;

public sealed class OrderPromotionRedeemer : IOrderPromotionRedeemer
{
    private readonly IPromotionRedemptionService _redemptionService;

    public OrderPromotionRedeemer(IPromotionRedemptionService redemptionService)
    {
        _redemptionService = redemptionService;
    }

    public Task RedeemCouponUsageAsync(
        Guid tenantId,
        Guid couponId,
        Guid orderId,
        CancellationToken cancellationToken)
    {
        return _redemptionService.RedeemCouponUsageAsync(tenantId, couponId, orderId, cancellationToken);
    }
}
