namespace Knight.Contracts.Fulfillment;

public sealed record TenantFulfillmentSettingsResponse
{
    public Guid TenantId { get; init; }
    public bool PickupEnabled { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? UpdatedAt { get; init; }
}
