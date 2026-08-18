namespace Knight.Contracts.Delivery;

public sealed record UpdateTenantDeliverySettingsRequest
{
    public bool IsAcceptingDeliveryOrders { get; init; } = true;
    public decimal? DefaultMinimumOrderSubtotal { get; init; }
}

public sealed record CreateDeliveryZoneRequest
{
    public string Name { get; init; } = string.Empty;
    public decimal Fee { get; init; }
    public decimal? MinimumOrderSubtotal { get; init; }
    public int DisplayOrder { get; init; }
}

public sealed record UpdateDeliveryZoneRequest
{
    public string Name { get; init; } = string.Empty;
    public decimal Fee { get; init; }
    public decimal? MinimumOrderSubtotal { get; init; }
    public int DisplayOrder { get; init; }
}
