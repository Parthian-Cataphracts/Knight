namespace Knight.Contracts.ControlPlane;

public sealed record CreateCustomerRequest
{
    public required string Name { get; init; }

    public string? LegalName { get; init; }

    public required string ContactEmail { get; init; }

    public string? Phone { get; init; }

    public string? Notes { get; init; }
}

public sealed record UpdateCustomerRequest
{
    public required string Name { get; init; }

    public string? LegalName { get; init; }

    public required string ContactEmail { get; init; }

    public string? Phone { get; init; }

    public string? Notes { get; init; }
}

public sealed record CustomerResponse
{
    public required Guid Id { get; init; }

    public required string Name { get; init; }

    public string? LegalName { get; init; }

    public required string ContactEmail { get; init; }

    public string? Phone { get; init; }

    public required string Status { get; init; }

    public string? Notes { get; init; }

    /// <summary>How many stores the customer has registered.</summary>
    public required int StoreCount { get; init; }

    /// <summary>Key of the plan the customer is currently on; null when they have no live subscription.</summary>
    public string? PlanKey { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset? UpdatedAt { get; init; }
}
