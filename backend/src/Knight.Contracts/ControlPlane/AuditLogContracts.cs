namespace Knight.Contracts.ControlPlane;

public sealed record AuditLogResponse
{
    public required Guid Id { get; init; }

    public required string ActorType { get; init; }

    public Guid? ActorUserId { get; init; }

    public string? ActorDisplay { get; init; }

    public Guid? CustomerId { get; init; }

    public required string Action { get; init; }

    public required string TargetType { get; init; }

    public string? TargetId { get; init; }

    /// <summary>Raw JSON documents, already scrubbed of anything credential-shaped when written.</summary>
    public string? PreviousValue { get; init; }

    public string? NewValue { get; init; }

    public string? CorrelationId { get; init; }

    public string? IpAddress { get; init; }

    public required DateTimeOffset OccurredAt { get; init; }
}
