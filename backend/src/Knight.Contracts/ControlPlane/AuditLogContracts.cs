namespace Knight.Contracts.ControlPlane;

public sealed record AuditLogResponse
{
    public required Guid Id { get; init; }

    public required string ActorType { get; init; }

    public Guid? ActorUserId { get; init; }

    public string? ActorDisplay { get; init; }

    public Guid? CustomerId { get; init; }

    /// <summary>The customer the action concerned; null for platform-wide actions.</summary>
    public string? CustomerName { get; init; }

    /// <summary>Display form of the actor, falling back to the actor type for automated work.</summary>
    public required string Actor { get; init; }

    /// <summary>Target type and identifier as one readable string.</summary>
    public required string Target { get; init; }

    /// <summary>
    /// Success or Failure. The audit trail records attempts as well as
    /// outcomes — a rejected login is a fact worth keeping — and the action
    /// name carries which one it was.
    /// </summary>
    public required string Result { get; init; }

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
