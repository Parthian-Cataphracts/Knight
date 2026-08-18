namespace Knight.Contracts.Customer;

public sealed record CustomerResponse
{
    public required Guid Id { get; init; }
    public required string DisplayName { get; init; }
    public string? Phone { get; init; }
    public string? Email { get; init; }
    public required string Status { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? UpdatedAt { get; init; }
    public DateTimeOffset? ArchivedAt { get; init; }
}
