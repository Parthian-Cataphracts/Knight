namespace Knight.Contracts.Promotions;

public sealed record CouponResponse(
    Guid Id,
    Guid TenantId,
    Guid PromotionId,
    string Code,
    string NormalizedCode,
    string Status,
    int? UsageLimitTotal,
    int UsedCount,
    DateTimeOffset? StartsAt,
    DateTimeOffset? EndsAt,
    DateTimeOffset CreatedAt,
    DateTimeOffset? UpdatedAt,
    DateTimeOffset? ArchivedAt);
