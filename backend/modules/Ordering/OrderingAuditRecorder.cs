using Knight.Application.Abstractions.Auditing;
using Knight.Application.Abstractions.Identity;

namespace Ordering;

/// <summary>
/// Shared audit recorder for Ordering module operations. Derives actor information
/// from <see cref="ICurrentUser"/> or accepts explicit actor overrides.
/// </summary>
public sealed class OrderingAuditRecorder
{
    private readonly ICurrentUser _currentUser;
    private readonly IAuditLogger _auditLogger;

    public OrderingAuditRecorder(ICurrentUser currentUser, IAuditLogger auditLogger)
    {
        _currentUser = currentUser;
        _auditLogger = auditLogger;
    }

    public Task RecordAsync(
        string action,
        Guid tenantId,
        string entityType,
        Guid entityId,
        CancellationToken cancellationToken,
        Guid? actorUserId = null,
        PrincipalType? actorPrincipalType = null,
        IReadOnlyDictionary<string, string>? metadata = null)
    {
        var userId = actorUserId ?? _currentUser.UserId;
        var principalType = actorPrincipalType ?? _currentUser.PrincipalType;

        var actorType = principalType switch
        {
            PrincipalType.PlatformAdmin => AuditActorType.PlatformAdmin,
            PrincipalType.TenantUser => AuditActorType.TenantUser,
            _ => AuditActorType.System
        };

        return _auditLogger.RecordAsync(new AuditEntry
        {
            ActorUserId = userId,
            ActorType = actorType,
            TenantId = tenantId,
            Action = action,
            EntityType = entityType,
            EntityId = entityId.ToString(),
            Metadata = metadata
        }, cancellationToken);
    }
}
