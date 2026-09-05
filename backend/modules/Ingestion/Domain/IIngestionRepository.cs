namespace Ingestion.Domain;

/// <summary>
/// How the log stream is narrowed. Every field is optional and ANDed: a null
/// leaves that dimension unfiltered.
///
/// <see cref="Level"/> matches one exact level; <see cref="MinSeverity"/> keeps
/// everything at or above a severity, which is how the errors, warnings and
/// alerts are separated from the noise (docs/risks.md §3.4). When both are given
/// the exact level wins, since it is the more specific request.
/// </summary>
public sealed record LogFilter(
    Guid? StoreId = null,
    string? Level = null,
    string? MinSeverity = null,
    string? Search = null,
    DateTimeOffset? From = null,
    DateTimeOffset? To = null);

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
        LogFilter filter,
        int page,
        int pageSize,
        CancellationToken cancellationToken);

    Task<IReadOnlyCollection<StoreLogEntry>> ExportLogsAsync(
        LogFilter filter,
        int max,
        CancellationToken cancellationToken);

    Task SaveChangesAsync(CancellationToken cancellationToken);
}
