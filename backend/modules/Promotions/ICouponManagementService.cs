using Knight.Contracts.Promotions;
using Promotions.Domain;

namespace Promotions;

public interface ICouponManagementService
{
    Task<CouponResponse> CreateCouponAsync(
        Guid tenantId,
        CreateCouponRequest request,
        CancellationToken cancellationToken);

    Task<CouponResponse> UpdateCouponAsync(
        Guid tenantId,
        Guid id,
        UpdateCouponRequest request,
        CancellationToken cancellationToken);

    Task<CouponResponse> ArchiveCouponAsync(
        Guid tenantId,
        Guid id,
        CancellationToken cancellationToken);

    Task<CouponResponse> GetCouponByIdAsync(
        Guid tenantId,
        Guid id,
        CancellationToken cancellationToken);

    Task<(IReadOnlyList<CouponResponse> Items, int TotalCount)> ListCouponsAsync(
        Guid tenantId,
        CouponListFilter filter,
        CancellationToken cancellationToken);
}
