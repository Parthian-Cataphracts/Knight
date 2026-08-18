namespace Knight.Contracts.Promotions;

public sealed record CreateCouponRequest(
    Guid PromotionId,
    string Code,
    int? UsageLimitTotal,
    DateTimeOffset? StartsAt,
    DateTimeOffset? EndsAt);

public sealed record UpdateCouponRequest(
    int? UsageLimitTotal,
    DateTimeOffset? StartsAt,
    DateTimeOffset? EndsAt);
