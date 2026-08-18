namespace Promotions.Domain;

public sealed record PromotionListFilter(
    PromotionStatus? Status = null,
    PromotionDiscountType? DiscountType = null,
    bool? RequiresCoupon = null,
    int Page = 1,
    int PageSize = 20);

public sealed record CouponListFilter(
    Guid? PromotionId = null,
    CouponStatus? Status = null,
    int Page = 1,
    int PageSize = 20);

public interface IPromotionRepository
{
    Task<Promotion?> GetByIdAsync(Guid tenantId, Guid id, CancellationToken cancellationToken);

    Task<(IReadOnlyList<Promotion> Items, int TotalCount)> ListPromotionsAsync(
        Guid tenantId,
        PromotionListFilter filter,
        CancellationToken cancellationToken);

    Task AddPromotionAsync(Promotion promotion, CancellationToken cancellationToken);

    Task<Coupon?> GetCouponByIdAsync(Guid tenantId, Guid id, CancellationToken cancellationToken);

    Task<Coupon?> GetCouponByIdForUpdateAsync(Guid tenantId, Guid id, CancellationToken cancellationToken);

    Task<Coupon?> GetCouponByNormalizedCodeAsync(Guid tenantId, string normalizedCode, CancellationToken cancellationToken);

    Task<Coupon?> GetCouponByNormalizedCodeForUpdateAsync(Guid tenantId, string normalizedCode, CancellationToken cancellationToken);

    Task<(IReadOnlyList<Coupon> Items, int TotalCount)> ListCouponsAsync(
        Guid tenantId,
        CouponListFilter filter,
        CancellationToken cancellationToken);

    Task AddCouponAsync(Coupon coupon, CancellationToken cancellationToken);

    Task<int> GetCouponRedemptionCountAsync(Guid tenantId, Guid couponId, CancellationToken cancellationToken);

    /// <summary>
    /// Redemption counts for a batch of coupons, keyed by coupon id. Coupons with no
    /// redemptions are simply absent from the result. Exists so listing a page of
    /// coupons costs one aggregate query rather than one count per row.
    /// </summary>
    Task<IReadOnlyDictionary<Guid, int>> GetCouponRedemptionCountsAsync(
        Guid tenantId,
        IReadOnlyCollection<Guid> couponIds,
        CancellationToken cancellationToken);

    Task<bool> HasOrderRedeemedCouponAsync(Guid tenantId, Guid couponId, Guid orderId, CancellationToken cancellationToken);

    Task AddCouponRedemptionAsync(CouponRedemption redemption, CancellationToken cancellationToken);

    Task<IReadOnlyList<Promotion>> GetActiveAutomaticPromotionsAsync(Guid tenantId, DateTimeOffset now, CancellationToken cancellationToken);

    Task SaveChangesAsync(CancellationToken cancellationToken);

    Task<TResult> ExecuteInTransactionAsync<TResult>(Func<Task<TResult>> operation, CancellationToken cancellationToken);
}
