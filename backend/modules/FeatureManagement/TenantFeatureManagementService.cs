using FeatureManagement.Domain;
using Knight.Application.Abstractions.Auditing;
using Knight.Application.Abstractions.Identity;
using Knight.Application.Abstractions.Time;
using Knight.Application.Exceptions;

namespace FeatureManagement;

public sealed class TenantFeatureManagementService : ITenantFeatureManagementService
{
    private readonly IFeatureStore _featureStore;
    private readonly IDateTimeProvider _dateTimeProvider;
    private readonly ICurrentUser _currentUser;
    private readonly IAuditLogger _auditLogger;

    public TenantFeatureManagementService(
        IFeatureStore featureStore,
        IDateTimeProvider dateTimeProvider,
        ICurrentUser currentUser,
        IAuditLogger auditLogger)
    {
        _featureStore = featureStore;
        _dateTimeProvider = dateTimeProvider;
        _currentUser = currentUser;
        _auditLogger = auditLogger;
    }

    public Task EnableAsync(Guid tenantId, string featureKey, CancellationToken cancellationToken) =>
        SetAsync(tenantId, featureKey, isEnabled: true, "TenantFeatureEnabled", cancellationToken);

    public Task DisableAsync(Guid tenantId, string featureKey, CancellationToken cancellationToken) =>
        SetAsync(tenantId, featureKey, isEnabled: false, "TenantFeatureDisabled", cancellationToken);

    private async Task SetAsync(Guid tenantId, string featureKey, bool isEnabled, string action, CancellationToken cancellationToken)
    {
        var normalizedKey = featureKey.Trim().ToLowerInvariant();

        var definition = await _featureStore.GetDefinitionAsync(normalizedKey, cancellationToken)
            ?? throw new NotFoundException(nameof(FeatureDefinition), normalizedKey);

        var now = _dateTimeProvider.UtcNow;
        await _featureStore.SetTenantFeatureAsync(tenantId, definition.Key, isEnabled, now, cancellationToken);

        await _auditLogger.RecordAsync(new AuditEntry
        {
            ActorUserId = _currentUser.UserId,
            ActorType = _currentUser.PrincipalType == PrincipalType.PlatformAdmin
                ? AuditActorType.PlatformAdmin
                : AuditActorType.System,
            TenantId = tenantId,
            Action = action,
            EntityType = nameof(TenantFeature),
            EntityId = definition.Key,
            Metadata = new Dictionary<string, string> { ["featureKey"] = definition.Key }
        }, cancellationToken);
    }
}
