using Knight.Application.Abstractions.Auditing;
using Knight.Application.Abstractions.Identity;

namespace Catalog;

/// <summary>
/// Shared audit call-site for the catalog services so actor derivation lives in
/// one place instead of being copy-pasted into every management service.
/// </summary>
public sealed class CatalogAuditRecorder
{
    private readonly ICurrentUser _currentUser;
    private readonly IAuditLogger _auditLogger;

    public CatalogAuditRecorder(ICurrentUser currentUser, IAuditLogger auditLogger)
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
        IReadOnlyDictionary<string, string>? metadata = null)
    {
        var isPlatformAdmin = _currentUser.PrincipalType == PrincipalType.PlatformAdmin;

        return _auditLogger.RecordAsync(new AuditEntry
        {
            ActorUserId = _currentUser.UserId,
            ActorType = isPlatformAdmin ? AuditActorType.PlatformAdmin : AuditActorType.TenantUser,
            TenantId = tenantId,
            Action = action,
            EntityType = entityType,
            EntityId = entityId.ToString(),
            Metadata = metadata
        }, cancellationToken);
    }
}

/// <summary>Shared paging bounds for catalog listings.</summary>
internal static class CatalogPaging
{
    internal const int MaxPageSize = 100;

    internal static (int Page, int PageSize) Bound(int page, int pageSize) =>
        (Math.Max(page, 1), Math.Clamp(pageSize, 1, MaxPageSize));
}
