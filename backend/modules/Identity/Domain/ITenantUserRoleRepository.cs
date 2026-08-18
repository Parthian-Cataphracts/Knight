namespace Identity.Domain;

/// <summary>
/// Persistence contract for the TenantUser↔Role assignment relationship and the
/// effective-permission resolution built on top of it.
/// </summary>
public interface ITenantUserRoleRepository
{
    Task<IReadOnlyCollection<Guid>> GetRoleIdsForUserAsync(Guid tenantId, Guid tenantUserId, CancellationToken cancellationToken);

    /// <summary>Atomically replaces the user's role assignments with exactly <paramref name="roleIds"/>.</summary>
    Task ReplaceUserRolesAsync(Guid tenantId, Guid tenantUserId, IReadOnlyCollection<Guid> roleIds, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>
    /// Resolves the distinct union of permission keys granted by every role
    /// currently assigned to the user, via one indexed join query — no N+1.
    /// </summary>
    Task<IReadOnlyCollection<string>> GetEffectivePermissionKeysAsync(Guid tenantId, Guid tenantUserId, CancellationToken cancellationToken);
}
