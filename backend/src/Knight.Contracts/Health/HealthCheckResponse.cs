namespace Knight.Contracts.Health;

public sealed record HealthCheckResponse
{
    public required string Status { get; init; }

    public required IReadOnlyCollection<HealthCheckEntry> Checks { get; init; }
}

public sealed record HealthCheckEntry
{
    public required string Name { get; init; }

    public required string Status { get; init; }

    public string? Description { get; init; }

    public double DurationMilliseconds { get; init; }
}
