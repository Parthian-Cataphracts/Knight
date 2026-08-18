namespace Knight.Application.Abstractions.Tenancy;

/// <summary>
/// Read-only view of the tenant the current request is operating within.
/// Application and module code must depend on this abstraction rather than
/// parsing hosts, headers, or claims directly.
/// </summary>
public interface ITenantContext
{
    /// <summary>
    /// The resolved tenant identifier, or <c>null</c> when no tenant applies
    /// to the current request (e.g. a Platform context request).
    /// </summary>
    Guid? TenantId { get; }

    bool HasTenant { get; }

    /// <summary>
    /// True only when the current request has been explicitly authorized as a
    /// Platform Super Admin operation spanning tenants. This must never be
    /// inferred from <see cref="HasTenant"/> being false — see
    /// docs/architecture/authorization.md.
    /// </summary>
    bool IsPlatformContext { get; }
}
