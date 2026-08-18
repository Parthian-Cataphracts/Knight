namespace Knight.Infrastructure.Auditing;

/// <summary>
/// Persisted record of a significant platform or tenant action. Deliberately not
/// tenant-scoped (<see cref="Tenancy.Domain.TenantId"/> is informational, not an
/// isolation boundary) since Platform Super Admin activity spans tenants and must
/// remain auditable regardless of the current tenant query filter.
/// </summary>
public sealed class AuditLogEntry
{
    public Guid Id { get; init; }

    public Guid? ActorUserId { get; init; }

    public required string ActorType { get; init; }

    public Guid? TenantId { get; init; }

    public required string Action { get; init; }

    public string? EntityType { get; init; }

    public string? EntityId { get; init; }

    public string? MetadataJson { get; init; }

    public required DateTimeOffset OccurredAt { get; init; }
}
