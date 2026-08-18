namespace Knight.Application.Abstractions.Tenancy;

/// <summary>
/// Mutating counterpart of <see cref="ITenantContext"/>. Only tenant-resolution
/// middleware and explicitly authorized platform-context handlers should depend
/// on this interface; ordinary application/module code must use
/// <see cref="ITenantContext"/> instead.
/// </summary>
public interface ITenantContextAccessor : ITenantContext
{
    void SetTenant(Guid tenantId);

    /// <summary>
    /// Explicitly elevates the current scope to a Platform context. Callers must
    /// have already authorized the caller as a Platform Super Admin; this method
    /// performs no authorization checks itself.
    /// </summary>
    void SetPlatformContext();

    void Clear();
}
