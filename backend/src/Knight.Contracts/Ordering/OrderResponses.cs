namespace Knight.Contracts.Ordering;

public sealed record OrderPartyResponse
{
    public Guid? SourceCustomerId { get; init; }
    public required string DisplayName { get; init; }
    public string? Phone { get; init; }
    public string? Email { get; init; }
}

public sealed record OrderDeliveryResponse
{
    public string? ZoneName { get; init; }
    public string? AddressLine1 { get; init; }
    public string? AddressLine2 { get; init; }
    public string? City { get; init; }
    public string? PostalCode { get; init; }
    public double? Latitude { get; init; }
    public double? Longitude { get; init; }
}

public sealed record OrderFulfillmentResponse
{
    public required string Method { get; init; }
    public required decimal Fee { get; init; }
    public OrderDeliveryResponse? Delivery { get; init; }
}

public sealed record OrderPromotionResponse
{
    public Guid? SourcePromotionId { get; init; }
    public Guid? SourceCouponId { get; init; }
    public required string PromotionName { get; init; }
    public string? CouponCode { get; init; }
    public required string DiscountType { get; init; }
    public required decimal DiscountValue { get; init; }
    public required decimal DiscountAmount { get; init; }
}

public sealed record OrderDetailResponse
{
    public required Guid Id { get; init; }
    public required long OrderNumber { get; init; }
    public required string Status { get; init; }
    public required string Currency { get; init; }
    public required decimal Subtotal { get; init; }
    public decimal DiscountTotal { get; init; }
    public decimal DiscountedSubtotal { get; init; }
    public required decimal FulfillmentFee { get; init; }
    public required decimal Total { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? UpdatedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
    public DateTimeOffset? CancelledAt { get; init; }
    public string? CancellationReason { get; init; }
    public OrderPartyResponse? Party { get; init; }
    public OrderFulfillmentResponse? Fulfillment { get; init; }
    public OrderPromotionResponse? Promotion { get; init; }
    public required IReadOnlyCollection<OrderItemResponse> Items { get; init; }
    public required IReadOnlyCollection<OrderStatusHistoryResponse> StatusHistory { get; init; }
}

public sealed record OrderSummaryResponse
{
    public required Guid Id { get; init; }
    public required long OrderNumber { get; init; }
    public required string Status { get; init; }
    public required string Currency { get; init; }
    public required decimal Subtotal { get; init; }
    public decimal DiscountTotal { get; init; }
    public decimal DiscountedSubtotal { get; init; }
    public required decimal Total { get; init; }
    public required int ItemCount { get; init; }
    public string? CustomerDisplayName { get; init; }
    public string? FulfillmentMethod { get; init; }
    public string? PromotionName { get; init; }
    public string? CouponCode { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
    public DateTimeOffset? CancelledAt { get; init; }
}

public sealed record OrderItemResponse
{
    public required Guid Id { get; init; }
    public required Guid SourceProductId { get; init; }
    public required string ProductName { get; init; }
    public Guid? SourceVariantId { get; init; }
    public string? VariantName { get; init; }
    public required decimal UnitBasePrice { get; init; }
    public required int Quantity { get; init; }
    public required decimal UnitModifierTotal { get; init; }
    public required decimal UnitPrice { get; init; }
    public required decimal LineTotal { get; init; }
    public required int DisplayOrder { get; init; }
    public required IReadOnlyCollection<OrderItemModifierResponse> Modifiers { get; init; }
}

public sealed record OrderItemModifierResponse
{
    public required Guid Id { get; init; }
    public required Guid SourceModifierGroupId { get; init; }
    public required string ModifierGroupName { get; init; }
    public required Guid SourceModifierId { get; init; }
    public required string ModifierName { get; init; }
    public required decimal UnitPriceDelta { get; init; }
    public required int DisplayOrder { get; init; }
}

public sealed record OrderStatusHistoryResponse
{
    public required Guid Id { get; init; }
    public string? FromStatus { get; init; }
    public required string ToStatus { get; init; }
    public required DateTimeOffset ChangedAt { get; init; }
    public Guid? ChangedByUserId { get; init; }
    public string? ChangedByPrincipalType { get; init; }
    public string? Reason { get; init; }
}
