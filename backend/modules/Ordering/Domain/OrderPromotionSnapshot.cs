using Knight.Domain.Common;

namespace Ordering.Domain;

public sealed class OrderPromotionSnapshot : Entity, ITenantScoped
{
    public Guid TenantId { get; private set; }
    public Guid OrderId { get; private set; }
    public Guid? SourcePromotionId { get; private set; }
    public Guid? SourceCouponId { get; private set; }
    public string PromotionName { get; private set; }
    public string? CouponCode { get; private set; }
    public string DiscountType { get; private set; }
    public decimal DiscountValue { get; private set; }
    public decimal DiscountAmount { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    private OrderPromotionSnapshot()
    {
        PromotionName = string.Empty;
        DiscountType = string.Empty;
    }

    private OrderPromotionSnapshot(
        Guid id,
        Guid tenantId,
        Guid orderId,
        Guid? sourcePromotionId,
        Guid? sourceCouponId,
        string promotionName,
        string? couponCode,
        string discountType,
        decimal discountValue,
        decimal discountAmount,
        DateTimeOffset createdAt)
        : base(id)
    {
        TenantId = tenantId;
        OrderId = orderId;
        SourcePromotionId = sourcePromotionId;
        SourceCouponId = sourceCouponId;
        PromotionName = promotionName;
        CouponCode = couponCode;
        DiscountType = discountType;
        DiscountValue = discountValue;
        DiscountAmount = discountAmount;
        CreatedAt = createdAt;
    }

    public static OrderPromotionSnapshot Create(
        Guid id,
        Guid tenantId,
        Guid orderId,
        Guid? sourcePromotionId,
        Guid? sourceCouponId,
        string promotionName,
        string? couponCode,
        string discountType,
        decimal discountValue,
        decimal discountAmount,
        DateTimeOffset createdAt)
    {
        if (id == Guid.Empty) throw new ArgumentException("Snapshot ID cannot be empty.", nameof(id));
        if (tenantId == Guid.Empty) throw new ArgumentException("Tenant ID cannot be empty.", nameof(tenantId));
        if (orderId == Guid.Empty) throw new ArgumentException("Order ID cannot be empty.", nameof(orderId));
        if (string.IsNullOrWhiteSpace(promotionName)) throw new ArgumentException("Promotion name is required.", nameof(promotionName));
        if (discountAmount < 0m) throw new ArgumentException("Discount amount cannot be negative.", nameof(discountAmount));

        return new OrderPromotionSnapshot(
            id,
            tenantId,
            orderId,
            sourcePromotionId,
            sourceCouponId,
            promotionName.Trim(),
            string.IsNullOrWhiteSpace(couponCode) ? null : couponCode.Trim().ToUpperInvariant(),
            discountType.Trim(),
            discountValue,
            discountAmount,
            createdAt);
    }
}
