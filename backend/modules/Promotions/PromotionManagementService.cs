using Knight.Application.Abstractions.Time;
using Knight.Application.Exceptions;
using Knight.Contracts.Promotions;
using Promotions.Domain;

namespace Promotions;

public sealed class PromotionManagementService : IPromotionManagementService
{
    private readonly IPromotionRepository _promotionRepository;
    private readonly IDateTimeProvider _dateTimeProvider;
    private readonly PromotionsAuditRecorder _auditRecorder;

    public PromotionManagementService(
        IPromotionRepository promotionRepository,
        IDateTimeProvider dateTimeProvider,
        PromotionsAuditRecorder auditRecorder)
    {
        _promotionRepository = promotionRepository;
        _dateTimeProvider = dateTimeProvider;
        _auditRecorder = auditRecorder;
    }

    public async Task<PromotionResponse> CreatePromotionAsync(
        Guid tenantId,
        CreatePromotionRequest request,
        CancellationToken cancellationToken)
    {
        if (tenantId == Guid.Empty) throw new ArgumentException("Tenant ID cannot be empty.", nameof(tenantId));

        if (!Enum.TryParse<PromotionDiscountType>(request.DiscountType, ignoreCase: true, out var discountType))
        {
            throw new ValidationException(new Dictionary<string, string[]>
            {
                ["discountType"] = [$"Invalid discount type '{request.DiscountType}'. Supported types are 'Percentage' and 'FixedAmount'."]
            });
        }

        var now = _dateTimeProvider.UtcNow;
        var promotionId = Guid.NewGuid();

        var promotion = Promotion.Create(
            promotionId,
            tenantId,
            request.Name,
            request.Description,
            discountType,
            request.DiscountValue,
            request.MinimumSubtotal,
            request.MaximumDiscountAmount,
            request.StartsAt,
            request.EndsAt,
            request.RequiresCoupon,
            request.Priority,
            now);

        await _promotionRepository.AddPromotionAsync(promotion, cancellationToken);
        await _promotionRepository.SaveChangesAsync(cancellationToken);

        await _auditRecorder.RecordAsync(
            "PromotionCreated",
            tenantId,
            "Promotion",
            promotionId,
            cancellationToken,
            metadata: new Dictionary<string, string>
            {
                ["Name"] = promotion.Name,
                ["DiscountType"] = promotion.DiscountType.ToString(),
                ["RequiresCoupon"] = promotion.RequiresCoupon.ToString()
            });

        return MapToResponse(promotion);
    }

    public async Task<PromotionResponse> UpdatePromotionAsync(
        Guid tenantId,
        Guid id,
        UpdatePromotionRequest request,
        CancellationToken cancellationToken)
    {
        if (tenantId == Guid.Empty) throw new ArgumentException("Tenant ID cannot be empty.", nameof(tenantId));
        if (id == Guid.Empty) throw new ArgumentException("Promotion ID cannot be empty.", nameof(id));

        if (!Enum.TryParse<PromotionDiscountType>(request.DiscountType, ignoreCase: true, out var discountType))
        {
            throw new ValidationException(new Dictionary<string, string[]>
            {
                ["discountType"] = [$"Invalid discount type '{request.DiscountType}'. Supported types are 'Percentage' and 'FixedAmount'."]
            });
        }

        var promotion = await _promotionRepository.GetByIdAsync(tenantId, id, cancellationToken);
        if (promotion is null)
        {
            throw new NotFoundException($"Promotion '{id}' was not found.");
        }

        var now = _dateTimeProvider.UtcNow;
        promotion.Update(
            request.Name,
            request.Description,
            discountType,
            request.DiscountValue,
            request.MinimumSubtotal,
            request.MaximumDiscountAmount,
            request.StartsAt,
            request.EndsAt,
            request.RequiresCoupon,
            request.Priority,
            now);

        await _promotionRepository.SaveChangesAsync(cancellationToken);

        await _auditRecorder.RecordAsync(
            "PromotionUpdated",
            tenantId,
            "Promotion",
            id,
            cancellationToken,
            metadata: new Dictionary<string, string>
            {
                ["Name"] = promotion.Name,
                ["DiscountType"] = promotion.DiscountType.ToString()
            });

        return MapToResponse(promotion);
    }

    public async Task<PromotionResponse> ActivatePromotionAsync(
        Guid tenantId,
        Guid id,
        CancellationToken cancellationToken)
    {
        if (tenantId == Guid.Empty) throw new ArgumentException("Tenant ID cannot be empty.", nameof(tenantId));
        if (id == Guid.Empty) throw new ArgumentException("Promotion ID cannot be empty.", nameof(id));

        var promotion = await _promotionRepository.GetByIdAsync(tenantId, id, cancellationToken);
        if (promotion is null)
        {
            throw new NotFoundException($"Promotion '{id}' was not found.");
        }

        var now = _dateTimeProvider.UtcNow;
        promotion.Activate(now);

        await _promotionRepository.SaveChangesAsync(cancellationToken);

        await _auditRecorder.RecordAsync(
            "PromotionUpdated",
            tenantId,
            "Promotion",
            id,
            cancellationToken,
            metadata: new Dictionary<string, string>
            {
                ["Status"] = promotion.Status.ToString()
            });

        return MapToResponse(promotion);
    }

    public async Task<PromotionResponse> ArchivePromotionAsync(
        Guid tenantId,
        Guid id,
        CancellationToken cancellationToken)
    {
        if (tenantId == Guid.Empty) throw new ArgumentException("Tenant ID cannot be empty.", nameof(tenantId));
        if (id == Guid.Empty) throw new ArgumentException("Promotion ID cannot be empty.", nameof(id));

        var promotion = await _promotionRepository.GetByIdAsync(tenantId, id, cancellationToken);
        if (promotion is null)
        {
            throw new NotFoundException($"Promotion '{id}' was not found.");
        }

        var now = _dateTimeProvider.UtcNow;
        promotion.Archive(now);

        await _promotionRepository.SaveChangesAsync(cancellationToken);

        await _auditRecorder.RecordAsync(
            "PromotionArchived",
            tenantId,
            "Promotion",
            id,
            cancellationToken);

        return MapToResponse(promotion);
    }

    public async Task<PromotionResponse> GetPromotionByIdAsync(
        Guid tenantId,
        Guid id,
        CancellationToken cancellationToken)
    {
        if (tenantId == Guid.Empty) throw new ArgumentException("Tenant ID cannot be empty.", nameof(tenantId));
        if (id == Guid.Empty) throw new ArgumentException("Promotion ID cannot be empty.", nameof(id));

        var promotion = await _promotionRepository.GetByIdAsync(tenantId, id, cancellationToken);
        if (promotion is null)
        {
            throw new NotFoundException($"Promotion '{id}' was not found.");
        }

        return MapToResponse(promotion);
    }

    public async Task<(IReadOnlyList<PromotionResponse> Items, int TotalCount)> ListPromotionsAsync(
        Guid tenantId,
        PromotionListFilter filter,
        CancellationToken cancellationToken)
    {
        if (tenantId == Guid.Empty) throw new ArgumentException("Tenant ID cannot be empty.", nameof(tenantId));

        var (items, totalCount) = await _promotionRepository.ListPromotionsAsync(tenantId, filter, cancellationToken);
        var mapped = items.Select(MapToResponse).ToList();
        return (mapped, totalCount);
    }

    private static PromotionResponse MapToResponse(Promotion p)
    {
        return new PromotionResponse(
            p.Id,
            p.TenantId,
            p.Name,
            p.Description,
            p.Status.ToString(),
            p.DiscountType.ToString(),
            p.DiscountValue,
            p.MinimumSubtotal,
            p.MaximumDiscountAmount,
            p.StartsAt,
            p.EndsAt,
            p.RequiresCoupon,
            p.Priority,
            p.CreatedAt,
            p.UpdatedAt,
            p.ArchivedAt);
    }
}
