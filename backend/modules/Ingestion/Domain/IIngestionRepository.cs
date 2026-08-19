namespace Ingestion.Domain;

/// <summary>
/// Persistence for everything a store pushes. Append-only, batched, and the
/// highest-volume write path in KNIGHT — which is why it is one repository with
/// one save rather than three: a batch of fifty errors is one round trip, not
/// fifty.
///
/// Implementations apply the caller's customer scope, so a store token cannot
/// write into — or read out of — another customer's rows.
/// </summary>
public interface IIngestionRepository
{
    Task AddErrorsAsync(IReadOnlyCollection<StoreErrorEvent> events, CancellationToken cancellationToken);

    Task AddEventsAsync(IReadOnlyCollection<StoreLifecycleEvent> events, CancellationToken cancellationToken);

    Task AddLogsAsync(IReadOnlyCollection<StoreLogEntry> entries, CancellationToken cancellationToken);

    Task<(IReadOnlyCollection<StoreErrorEvent> Items, long TotalCount)> ListErrorsAsync(
        Guid? storeId,
        int page,
        int pageSize,
        CancellationToken cancellationToken);

    Task<(IReadOnlyCollection<StoreLifecycleEvent> Items, long TotalCount)> ListEventsAsync(
        Guid? storeId,
        int page,
        int pageSize,
        CancellationToken cancellationToken);

    Task<(IReadOnlyCollection<StoreLogEntry> Items, long TotalCount)> ListLogsAsync(
        Guid? storeId,
        string? level,
        int page,
        int pageSize,
        CancellationToken cancellationToken);

    Task SaveChangesAsync(CancellationToken cancellationToken);
}
