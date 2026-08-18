using Knight.Domain.Common;

namespace Promotions.Domain;

public sealed class CouponRedemption : Entity, ITenantScoped
{
    public Guid TenantId { get; private set; }
    public Guid CouponId { get; private set; }
    public Guid OrderId { get; private set; }
    public DateTimeOffset RedeemedAt { get; private set; }

    private CouponRedemption()
    {
    }

    private CouponRedemption(
        Guid id,
        Guid tenantId,
        Guid couponId,
        Guid orderId,
        DateTimeOffset redeemedAt)
        : base(id)
    {
        TenantId = tenantId;
        CouponId = couponId;
        OrderId = orderId;
        RedeemedAt = redeemedAt;
    }

    public static CouponRedemption Create(
        Guid id,
        Guid tenantId,
        Guid couponId,
        Guid orderId,
        DateTimeOffset redeemedAt)
    {
        if (id == Guid.Empty) throw new ArgumentException("Redemption ID cannot be empty.", nameof(id));
        if (tenantId == Guid.Empty) throw new ArgumentException("Tenant ID cannot be empty.", nameof(tenantId));
        if (couponId == Guid.Empty) throw new ArgumentException("Coupon ID cannot be empty.", nameof(couponId));
        if (orderId == Guid.Empty) throw new ArgumentException("Order ID cannot be empty.", nameof(orderId));

        return new CouponRedemption(id, tenantId, couponId, orderId, redeemedAt);
    }
}
