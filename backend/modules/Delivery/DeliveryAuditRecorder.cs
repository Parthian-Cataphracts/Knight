using Knight.Application.Abstractions.Auditing;
using Knight.Application.Abstractions.Identity;

namespace Delivery;

public sealed class DeliveryAuditRecorder
{
    private readonly ICurrentUser _currentUser;
    private readonly IAuditLogger _auditLogger;

    public DeliveryAuditRecorder(ICurrentUser currentUser, IAuditLogger auditLogger)
    {
        _currentUser = currentUser;
        _auditLogger = auditLogger;
    }

    public Task RecordAsync(
        string action,
        string entityType,
        Guid entityId,
        Guid tenantId,
        CancellationToken cancellationToken,
        Guid? actorUserId = null,
        PrincipalType? actorPrincipalType = null,
        IReadOnlyDictionary<string, string>? nonPiiMetadata = null)
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
            Metadata = nonPiiMetadata
        }, cancellationToken);
    }
}
