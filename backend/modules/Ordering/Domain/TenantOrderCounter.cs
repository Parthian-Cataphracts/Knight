using Knight.Domain.Common;

namespace Ordering.Domain;

/// <summary>
/// Tenant-level sequence state for allocating monotonic, concurrency-safe human-facing order numbers.
/// </summary>
public sealed class TenantOrderCounter : Entity, ITenantScoped
{
    public Guid TenantId { get; private set; }

    public long NextOrderNumber { get; private set; }

    private TenantOrderCounter()
    {
    }

    public TenantOrderCounter(Guid tenantId, long nextOrderNumber)
        : base(tenantId)
    {
        TenantId = tenantId;
        NextOrderNumber = nextOrderNumber;
    }
}
