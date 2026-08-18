namespace Promotions.Domain;

public enum PromotionStatus
{
    Draft = 1,
    Active = 2,
    Archived = 3
}

public enum PromotionDiscountType
{
    Percentage = 1,
    FixedAmount = 2
}

public enum CouponStatus
{
    Active = 1,
    Archived = 2
}
