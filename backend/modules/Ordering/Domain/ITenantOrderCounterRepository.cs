namespace Ordering.Domain;

public interface ITenantOrderCounterRepository
{
    /// <summary>
    /// Atomically increments and returns the next tenant-scoped order number starting at 1001.
    /// Concurrency-safe in PostgreSQL via atomic upsert with RETURNING.
    /// </summary>
    Task<long> NextOrderNumberAsync(Guid tenantId, CancellationToken cancellationToken);
}
