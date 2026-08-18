namespace Knight.Contracts.Fulfillment;

public sealed record UpdateTenantFulfillmentSettingsRequest
{
    public bool PickupEnabled { get; init; } = true;
}
