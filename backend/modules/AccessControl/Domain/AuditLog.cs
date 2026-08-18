using Knight.Application.Abstractions.ControlPlane;
using Knight.Domain.Common;
using Knight.Domain.Exceptions;

namespace AccessControl.Domain;

/// <summary>
/// Who did what to which resource. Written for every state-changing
/// administrative action and for reads of sensitive data such as issuing a
/// credential (docs/authorization.md section 7).
///
/// Entries are append-only and never contain secrets, tokens or password
/// hashes: callers pass redacted before/after documents, and the aggregate has
/// no operation that mutates a recorded row.
/// </summary>
public sealed class AuditLog : Entity, ICustomerScoped
{
    public Guid? ActorUserId { get; private set; }

    public AuditActorType ActorType { get; private set; }

    /// <summary>Display form of the actor captured at the time, so the entry stays readable after the account is renamed or removed.</summary>
    public string? ActorDisplay { get; private set; }

    /// <summary>Customer the action concerned; null for platform-wide actions.</summary>
    public Guid? CustomerId { get; private set; }

    public string Action { get; private set; }

    public string TargetType { get; private set; }

    public string? TargetId { get; private set; }

    public string? PreviousValue { get; private set; }

    public string? NewValue { get; private set; }

    public string? CorrelationId { get; private set; }

    public string? IpAddress { get; private set; }

    public DateTimeOffset OccurredAt { get; private set; }

    private AuditLog()
    {
        Action = string.Empty;
        TargetType = string.Empty;
    }

    private AuditLog(
        Guid id,
        Guid? actorUserId,
        AuditActorType actorType,
        string? actorDisplay,
        Guid? customerId,
        string action,
        string targetType,
        string? targetId,
        string? previousValue,
        string? newValue,
        string? correlationId,
        string? ipAddress,
        DateTimeOffset occurredAt)
        : base(id)
    {
        ActorUserId = actorUserId;
        ActorType = actorType;
        ActorDisplay = actorDisplay;
        CustomerId = customerId;
        Action = action;
        TargetType = targetType;
        TargetId = targetId;
        PreviousValue = previousValue;
        NewValue = newValue;
        CorrelationId = correlationId;
        IpAddress = ipAddress;
        OccurredAt = occurredAt;
    }

    public static AuditLog Record(
        Guid id,
        AuditActorType actorType,
        Guid? actorUserId,
        string? actorDisplay,
        Guid? customerId,
        string action,
        string targetType,
        string? targetId,
        DateTimeOffset occurredAt,
        string? previousValue = null,
        string? newValue = null,
        string? correlationId = null,
        string? ipAddress = null)
    {
        if (string.IsNullOrWhiteSpace(action))
        {
            throw DomainException.Validation("An audit entry requires an action.");
        }

        if (string.IsNullOrWhiteSpace(targetType))
        {
            throw DomainException.Validation("An audit entry requires a target type.");
        }

        if (actorType is AuditActorType.User && actorUserId is null)
        {
            throw DomainException.Validation("A user-attributed audit entry requires the acting account.");
        }

        return new AuditLog(
            id,
            actorUserId,
            actorType,
            actorDisplay?.Trim(),
            customerId,
            action.Trim(),
            targetType.Trim(),
            targetId?.Trim(),
            previousValue,
            newValue,
            correlationId,
            ipAddress,
            occurredAt);
    }
}
