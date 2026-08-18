namespace Knight.Contracts.Customer;

public sealed record CreateCustomerRequest
{
    public required string DisplayName { get; init; }
    public string? Phone { get; init; }
    public string? Email { get; init; }
}

public sealed record UpdateCustomerRequest
{
    public required string DisplayName { get; init; }
    public string? Phone { get; init; }
    public string? Email { get; init; }
}
