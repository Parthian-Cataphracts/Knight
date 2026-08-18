using Knight.Application.Abstractions.Tenancy;

namespace Tenancy;

/// <summary>
/// Scoped implementation of the tenant context. Register once per request scope;
/// only tenant-resolution middleware should call the mutating members.
/// </summary>
public sealed class TenantContextAccessor : ITenantContextAccessor
{
    public Guid? TenantId { get; private set; }

    public bool HasTenant => TenantId.HasValue;

    public bool IsPlatformContext { get; private set; }

    public void SetTenant(Guid tenantId)
    {
        TenantId = tenantId;
        IsPlatformContext = false;
    }

    public void SetPlatformContext()
    {
        TenantId = null;
        IsPlatformContext = true;
    }

    public void Clear()
    {
        TenantId = null;
        IsPlatformContext = false;
    }
}
