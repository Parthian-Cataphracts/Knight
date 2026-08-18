using Knight.Application.Abstractions.Auditing;
using Knight.Application.Abstractions.Identity;

namespace Customer;

public sealed class CustomerAuditRecorder
{
    private const string EntityType = "Customer";
    private readonly ICurrentUser _currentUser;
    private readonly IAuditLogger _auditLogger;

    public CustomerAuditRecorder(ICurrentUser currentUser, IAuditLogger auditLogger)
    {
        _currentUser = currentUser;
        _auditLogger = auditLogger;
    }

    public Task RecordAsync(
        string action,
        Guid tenantId,
        Guid customerId,
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
            EntityType = EntityType,
            EntityId = customerId.ToString(),
            Metadata = nonPiiMetadata
        }, cancellationToken);
    }
}
