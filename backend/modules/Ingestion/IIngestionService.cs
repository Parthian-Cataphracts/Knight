using Ingestion.Domain;

namespace Ingestion;

/// <summary>
/// Who is ingesting. Established by the pipeline from the store token and passed
/// in rather than read from HTTP, so the service can be exercised — and reasoned
/// about — without a request.
/// </summary>
public sealed record IngestingStore(Guid StoreId, Guid CustomerId, string Environment);

public sealed record ErrorEventInput(
    DateTimeOffset OccurredAt,
    string? ExceptionType,
    string? Message,
    string? Endpoint,
    string? HttpMethod,
    int? StatusCode,
    string? StackTrace,
    string? RequestId,
    string? TraceId,
    string? ContextJson);

public sealed record LifecycleEventInput(
    DateTimeOffset OccurredAt,
    string? Type,
    string? Severity,
    string? Summary,
    string? TraceId,
    string? PayloadJson);

public sealed record LogEntryInput(
    DateTimeOffset Timestamp,
    string? Level,
    string? Service,
    string? Message,
    string? RequestId,
    string? TraceId,
    string? Exception,
    string? AttributesJson);

/// <summary>
/// What a batch did. <paramref name="Duplicate"/> is true when the batch was
/// recognised by its idempotency key and nothing was written a second time —
/// the caller still gets a success, because from the store's point of view the
/// batch did arrive.
/// </summary>
public sealed record IngestionReceipt(int Accepted, int Rejected, bool Duplicate, IReadOnlyCollection<string> Errors)
{
    public static IngestionReceipt DuplicateBatch { get; } = new(0, 0, true, []);
}

/// <summary>
/// Everything a store pushes into KNIGHT (docs/api-contracts.md §3).
///
/// Three rules apply to every batch and are enforced here rather than per
/// endpoint: the payload's environment must match the one the token was minted
/// for, the batch must be within its cap, and a batch replayed under the same
/// idempotency key is acknowledged without being written twice.
/// </summary>
public interface IIngestionService
{
    Task<IngestionReceipt> IngestErrorsAsync(
        IngestingStore store,
        string payloadEnvironment,
        string? storeVersion,
        IReadOnlyCollection<ErrorEventInput> events,
        string? idempotencyKey,
        CancellationToken cancellationToken);

    Task<IngestionReceipt> IngestEventsAsync(
        IngestingStore store,
        string payloadEnvironment,
        IReadOnlyCollection<LifecycleEventInput> events,
        string? idempotencyKey,
        CancellationToken cancellationToken);

    /// <summary>Refused with <see cref="Knight.Application.Exceptions.ForbiddenException"/> when the customer is not entitled to log shipping.</summary>
    Task<IngestionReceipt> IngestLogsAsync(
        IngestingStore store,
        string payloadEnvironment,
        string? storeVersion,
        IReadOnlyCollection<LogEntryInput> entries,
        string? idempotencyKey,
        CancellationToken cancellationToken);

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
}
