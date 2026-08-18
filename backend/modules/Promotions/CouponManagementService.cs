using Knight.Application.Abstractions.Time;
using Knight.Application.Exceptions;
using Knight.Contracts.Promotions;
using Promotions.Domain;

namespace Promotions;

public sealed class CouponManagementService : ICouponManagementService
{
    private readonly IPromotionRepository _promotionRepository;
    private readonly IDateTimeProvider _dateTimeProvider;
    private readonly PromotionsAuditRecorder _auditRecorder;

    public CouponManagementService(
        IPromotionRepository promotionRepository,
        IDateTimeProvider dateTimeProvider,
        PromotionsAuditRecorder auditRecorder)
    {
        _promotionRepository = promotionRepository;
        _dateTimeProvider = dateTimeProvider;
        _auditRecorder = auditRecorder;
    }

    public async Task<CouponResponse> CreateCouponAsync(
        Guid tenantId,
        CreateCouponRequest request,
        CancellationToken cancellationToken)
    {
        if (tenantId == Guid.Empty) throw new ArgumentException("Tenant ID cannot be empty.", nameof(tenantId));
        if (request.PromotionId == Guid.Empty) throw new ArgumentException("Promotion ID cannot be empty.", nameof(request.PromotionId));

        var promotion = await _promotionRepository.GetByIdAsync(tenantId, request.PromotionId, cancellationToken);
        if (promotion is null)
        {
            throw new NotFoundException($"Promotion '{request.PromotionId}' was not found.");
        }

        if (promotion.Status == PromotionStatus.Archived)
        {
            throw new ConflictException("Cannot create a coupon for an archived promotion.");
        }

        var normalizedCode = Coupon.NormalizeCode(request.Code);
        if (string.IsNullOrWhiteSpace(normalizedCode))
        {
            throw new ValidationException(new Dictionary<string, string[]>
            {
                ["code"] = ["Coupon code cannot be empty."]
            });
        }

        var existingCoupon = await _promotionRepository.GetCouponByNormalizedCodeAsync(tenantId, normalizedCode, cancellationToken);
        if (existingCoupon is not null)
        {
            throw new ConflictException($"A coupon with code '{request.Code.Trim()}' already exists for this tenant.");
        }

        var now = _dateTimeProvider.UtcNow;
        var couponId = Guid.NewGuid();

        var coupon = Coupon.Create(
            couponId,
            tenantId,
            request.PromotionId,
            request.Code,
            request.UsageLimitTotal,
            request.StartsAt,
            request.EndsAt,
            now);

        await _promotionRepository.AddCouponAsync(coupon, cancellationToken);
        await _promotionRepository.SaveChangesAsync(cancellationToken);

        await _auditRecorder.RecordAsync(
            "CouponCreated",
            tenantId,
            "Coupon",
            couponId,
            cancellationToken,
            metadata: new Dictionary<string, string>
            {
                ["Code"] = coupon.Code,
                ["PromotionId"] = coupon.PromotionId.ToString()
            });

        return MapToResponse(coupon, 0);
    }

    public async Task<CouponResponse> UpdateCouponAsync(
        Guid tenantId,
        Guid id,
        UpdateCouponRequest request,
        CancellationToken cancellationToken)
    {
        if (tenantId == Guid.Empty) throw new ArgumentException("Tenant ID cannot be empty.", nameof(tenantId));
        if (id == Guid.Empty) throw new ArgumentException("Coupon ID cannot be empty.", nameof(id));

        var coupon = await _promotionRepository.GetCouponByIdAsync(tenantId, id, cancellationToken);
        if (coupon is null)
        {
            throw new NotFoundException($"Coupon '{id}' was not found.");
        }

        var now = _dateTimeProvider.UtcNow;
        coupon.Update(
            request.UsageLimitTotal,
            request.StartsAt,
            request.EndsAt,
            now);

        await _promotionRepository.SaveChangesAsync(cancellationToken);

        await _auditRecorder.RecordAsync(
            "CouponUpdated",
            tenantId,
            "Coupon",
            id,
            cancellationToken);

        var usedCount = await _promotionRepository.GetCouponRedemptionCountAsync(tenantId, coupon.Id, cancellationToken);
        return MapToResponse(coupon, usedCount);
    }

    public async Task<CouponResponse> ArchiveCouponAsync(
        Guid tenantId,
        Guid id,
        CancellationToken cancellationToken)
    {
        if (tenantId == Guid.Empty) throw new ArgumentException("Tenant ID cannot be empty.", nameof(tenantId));
        if (id == Guid.Empty) throw new ArgumentException("Coupon ID cannot be empty.", nameof(id));

        var coupon = await _promotionRepository.GetCouponByIdAsync(tenantId, id, cancellationToken);
        if (coupon is null)
        {
            throw new NotFoundException($"Coupon '{id}' was not found.");
        }

        var now = _dateTimeProvider.UtcNow;
        coupon.Archive(now);

        await _promotionRepository.SaveChangesAsync(cancellationToken);

        await _auditRecorder.RecordAsync(
            "CouponArchived",
            tenantId,
            "Coupon",
            id,
            cancellationToken);

        var usedCount = await _promotionRepository.GetCouponRedemptionCountAsync(tenantId, coupon.Id, cancellationToken);
        return MapToResponse(coupon, usedCount);
    }

    public async Task<CouponResponse> GetCouponByIdAsync(
        Guid tenantId,
        Guid id,
        CancellationToken cancellationToken)
    {
        if (tenantId == Guid.Empty) throw new ArgumentException("Tenant ID cannot be empty.", nameof(tenantId));
        if (id == Guid.Empty) throw new ArgumentException("Coupon ID cannot be empty.", nameof(id));

        var coupon = await _promotionRepository.GetCouponByIdAsync(tenantId, id, cancellationToken);
        if (coupon is null)
        {
            throw new NotFoundException($"Coupon '{id}' was not found.");
        }

        var usedCount = await _promotionRepository.GetCouponRedemptionCountAsync(tenantId, coupon.Id, cancellationToken);
        return MapToResponse(coupon, usedCount);
    }

    public async Task<(IReadOnlyList<CouponResponse> Items, int TotalCount)> ListCouponsAsync(
        Guid tenantId,
        CouponListFilter filter,
        CancellationToken cancellationToken)
    {
        if (tenantId == Guid.Empty) throw new ArgumentException("Tenant ID cannot be empty.", nameof(tenantId));

        var (items, totalCount) = await _promotionRepository.ListCouponsAsync(tenantId, filter, cancellationToken);

        // One aggregate query for the whole page rather than a count per coupon.
        var redemptionCounts = await _promotionRepository.GetCouponRedemptionCountsAsync(
            tenantId,
            items.Select(c => c.Id).ToArray(),
            cancellationToken);

        var mapped = items
            .Select(c => MapToResponse(c, redemptionCounts.TryGetValue(c.Id, out var used) ? used : 0))
            .ToList();

        return (mapped, totalCount);
    }

    private static CouponResponse MapToResponse(Coupon c, int usedCount)
    {
        return new CouponResponse(
            c.Id,
            c.TenantId,
            c.PromotionId,
            c.Code,
            c.NormalizedCode,
            c.Status.ToString(),
            c.UsageLimitTotal,
            usedCount,
            c.StartsAt,
            c.EndsAt,
            c.CreatedAt,
            c.UpdatedAt,
            c.ArchivedAt);
    }
}
