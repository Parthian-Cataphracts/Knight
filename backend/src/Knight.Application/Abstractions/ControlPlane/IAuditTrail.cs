namespace Knight.Application.Abstractions.ControlPlane;

/// <summary>
/// Which kind of principal performed an audited action
/// (docs/domain-model.md section 9).
/// </summary>
public enum AuditActorType
{
    User = 0,
    System = 1,
    Store = 2,
    Agent = 3,
}

/// <summary>
/// Describes the acting principal for auditing. Populated from the request
/// pipeline; application and domain code never read HTTP themselves.
/// </summary>
public interface IAuditContext
{
    AuditActorType ActorType { get; }

    Guid? ActorUserId { get; }

    string? ActorDisplay { get; }

    string? CorrelationId { get; }

    string? IpAddress { get; }
}

/// <summary>
/// The single write path for control-plane audit entries. It lives in the
/// application layer rather than in one module so every module can record
/// without depending on the module that owns the audit table.
///
/// Callers pass ordinary before/after objects; the implementation redacts
/// anything that looks like a credential before storing it. Nothing that reaches
/// this interface may contain a secret in a field whose name does not say so
/// (docs/authorization.md section 7).
/// </summary>
public interface IAuditTrail
{
    Task RecordAsync(
        string action,
        string targetType,
        string? targetId,
        Guid? customerId,
        CancellationToken cancellationToken,
        object? previousValue = null,
        object? newValue = null);
}
