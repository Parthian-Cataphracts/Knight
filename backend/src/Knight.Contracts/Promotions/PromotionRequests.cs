namespace Knight.Contracts.Promotions;

public sealed record CreatePromotionRequest(
    string Name,
    string? Description,
    string DiscountType,
    decimal DiscountValue,
    decimal? MinimumSubtotal,
    decimal? MaximumDiscountAmount,
    DateTimeOffset? StartsAt,
    DateTimeOffset? EndsAt,
    bool RequiresCoupon,
    int Priority = 0);

public sealed record UpdatePromotionRequest(
    string Name,
    string? Description,
    string DiscountType,
    decimal DiscountValue,
    decimal? MinimumSubtotal,
    decimal? MaximumDiscountAmount,
    DateTimeOffset? StartsAt,
    DateTimeOffset? EndsAt,
    bool RequiresCoupon,
    int Priority = 0);
