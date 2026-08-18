namespace Knight.Contracts.Delivery;

public sealed record TenantDeliverySettingsResponse
{
    public Guid TenantId { get; init; }
    public bool IsAcceptingDeliveryOrders { get; init; }
    public decimal? DefaultMinimumOrderSubtotal { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? UpdatedAt { get; init; }
}

public sealed record DeliveryZoneResponse
{
    public Guid Id { get; init; }
    public string Name { get; init; } = string.Empty;
    public decimal Fee { get; init; }
    public decimal? MinimumOrderSubtotal { get; init; }
    public string Status { get; init; } = string.Empty;
    public int DisplayOrder { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? UpdatedAt { get; init; }
    public DateTimeOffset? ArchivedAt { get; init; }
}
