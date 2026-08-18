namespace Knight.Contracts.Promotions;

public sealed record PromotionResponse(
    Guid Id,
    Guid TenantId,
    string Name,
    string? Description,
    string Status,
    string DiscountType,
    decimal DiscountValue,
    decimal? MinimumSubtotal,
    decimal? MaximumDiscountAmount,
    DateTimeOffset? StartsAt,
    DateTimeOffset? EndsAt,
    bool RequiresCoupon,
    int Priority,
    DateTimeOffset CreatedAt,
    DateTimeOffset? UpdatedAt,
    DateTimeOffset? ArchivedAt);
