namespace Identity;

/// <summary>
/// Resolves a TenantUser's effective permission set: the distinct union of
/// permission keys granted by every role currently assigned to them within
/// their own tenant. See docs/architecture/authorization.md.
/// </summary>
public interface IEffectivePermissionService
{
    Task<IReadOnlyCollection<string>> GetEffectivePermissionKeysAsync(Guid tenantId, Guid tenantUserId, CancellationToken cancellationToken);
}
