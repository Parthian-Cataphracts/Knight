namespace Knight.Domain.Common;

/// <summary>
/// Marks an entity as owned by a single tenant. Implementing this interface is what
/// makes an entity eligible for centralized tenant-isolation enforcement (see
/// docs/architecture/multi-tenancy.md). Do not query these entities without going
/// through the tenant-aware persistence layer.
/// </summary>
public interface ITenantScoped
{
    Guid TenantId { get; }
}
