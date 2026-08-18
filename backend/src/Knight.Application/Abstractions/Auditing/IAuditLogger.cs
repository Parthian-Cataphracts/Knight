namespace Knight.Application.Abstractions.Auditing;

public enum AuditActorType
{
    PlatformAdmin,
    TenantUser,
    System
}

public sealed record AuditEntry
{
    public required Guid? ActorUserId { get; init; }

    public required AuditActorType ActorType { get; init; }

    public Guid? TenantId { get; init; }

    public required string Action { get; init; }

    public string? EntityType { get; init; }

    public string? EntityId { get; init; }

    public IReadOnlyDictionary<string, string>? Metadata { get; init; }
}

/// <summary>
/// Records platform and tenant activity for audit purposes. Implementations must
/// never receive or persist secrets (passwords, tokens, payment details).
/// </summary>
public interface IAuditLogger
{
    Task RecordAsync(AuditEntry entry, CancellationToken cancellationToken);
}
