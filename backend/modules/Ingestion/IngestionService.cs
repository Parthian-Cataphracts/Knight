using Ingestion.Domain;
using Knight.Application.Abstractions.ControlPlane;
using Knight.Application.Abstractions.Time;
using Knight.Application.Exceptions;
using Knight.Domain.Exceptions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Ingestion;

/// <summary>
/// Accepts what stores push.
///
/// The shape of this class follows from one decision: a store must never be able
/// to lose its own telemetry because one item in a batch was malformed. Bad
/// items are counted and described in the receipt while the rest are written, so
/// a store with one broken code path still reports the other forty-nine errors
/// in the batch. The exceptions are batch-level faults — wrong environment,
/// oversized batch, missing entitlement — where nothing in the batch can be
/// trusted or is permitted, and the whole thing is refused.
/// </summary>
internal sealed class IngestionService : IIngestionService
{
    /// <summary>The capability that must be entitled before a store may ship logs (docs/api-contracts.md §3).</summary>
    public const string LogShippingFeatureSlug = "log-shipping";

    private const int MaxPageSize = 200;

    private readonly IIngestionRepository _repository;
    private readonly ICustomerEntitlementReader _entitlements;
    private readonly IReplayGuard _replay;
    private readonly IDateTimeProvider _clock;
    private readonly ILogger<IngestionService> _logger;
    private readonly IngestionOptions _options;

    public IngestionService(
        IIngestionRepository repository,
        ICustomerEntitlementReader entitlements,
        IReplayGuard replay,
        IDateTimeProvider clock,
        ILogger<IngestionService> logger,
        IOptions<IngestionOptions> options)
    {
        _repository = repository;
        _entitlements = entitlements;
        _replay = replay;
        _clock = clock;
        _logger = logger;
        _options = options.Value;
    }

    public async Task<IngestionReceipt> IngestErrorsAsync(
        IngestingStore store,
        string payloadEnvironment,
        string? storeVersion,
        IReadOnlyCollection<ErrorEventInput> events,
        string? idempotencyKey,
        CancellationToken cancellationToken)
    {
        RequireMatchingEnvironment(store, payloadEnvironment);
        RequireWithinCap(events.Count, _options.MaxErrorsPerBatch, "events");

        if (!await ClaimAsync(store, "errors", idempotencyKey, cancellationToken))
        {
            return IngestionReceipt.DuplicateBatch;
        }

        var now = _clock.UtcNow;
        var accepted = new List<StoreErrorEvent>(events.Count);
        var errors = new List<string>();

        foreach (var input in events)
        {
            try
            {
                accepted.Add(StoreErrorEvent.Record(
                    Guid.NewGuid(),
                    store.StoreId,
                    store.CustomerId,
                    input.OccurredAt,
                    now,
                    store.Environment,
                    storeVersion,
                    input.ExceptionType,
                    input.Message,
                    input.Endpoint,
                    input.HttpMethod,
                    input.StatusCode,
                    input.StackTrace,
                    input.RequestId,
                    input.TraceId,
                    input.ContextJson));
            }
            catch (DomainException exception)
            {
                errors.Add(exception.Message);
            }
        }

        if (accepted.Count > 0)
        {
            await _repository.AddErrorsAsync(accepted, cancellationToken);
            await _repository.SaveChangesAsync(cancellationToken);
        }

        if (errors.Count > 0)
        {
            _logger.LogWarning(
                "Rejected {RejectedCount} of {TotalCount} error events from store {StoreId}",
                errors.Count,
                events.Count,
                store.StoreId);
        }

        return new IngestionReceipt(accepted.Count, errors.Count, false, errors);
    }

    public async Task<IngestionReceipt> IngestEventsAsync(
        IngestingStore store,
        string payloadEnvironment,
        IReadOnlyCollection<LifecycleEventInput> events,
        string? idempotencyKey,
        CancellationToken cancellationToken)
    {
        RequireMatchingEnvironment(store, payloadEnvironment);
        RequireWithinCap(events.Count, _options.MaxEventsPerBatch, "events");

        if (!await ClaimAsync(store, "events", idempotencyKey, cancellationToken))
        {
            return IngestionReceipt.DuplicateBatch;
        }

        var now = _clock.UtcNow;
        var accepted = new List<StoreLifecycleEvent>(events.Count);
        var errors = new List<string>();

        foreach (var input in events)
        {
            try
            {
                var severity = Enum.TryParse<StoreEventSeverity>(input.Severity, ignoreCase: true, out var parsed)
                    ? parsed
                    : StoreEventSeverity.Info;

                accepted.Add(StoreLifecycleEvent.Record(
                    Guid.NewGuid(),
                    store.StoreId,
                    store.CustomerId,
                    input.OccurredAt,
                    now,
                    input.Type,
                    severity,
                    input.Summary,
                    store.Environment,
                    input.TraceId,
                    input.PayloadJson));
            }
            catch (DomainException exception)
            {
                errors.Add(exception.Message);
            }
        }

        if (accepted.Count > 0)
        {
            await _repository.AddEventsAsync(accepted, cancellationToken);
            await _repository.SaveChangesAsync(cancellationToken);
        }

        return new IngestionReceipt(accepted.Count, errors.Count, false, errors);
    }

    public async Task<IngestionReceipt> IngestLogsAsync(
        IngestingStore store,
        string payloadEnvironment,
        string? storeVersion,
        IReadOnlyCollection<LogEntryInput> entries,
        string? idempotencyKey,
        CancellationToken cancellationToken)
    {
        RequireMatchingEnvironment(store, payloadEnvironment);
        RequireWithinCap(entries.Count, _options.MaxLogsPerBatch, "entries");

        // Checked before the idempotency claim: a refused batch must stay
        // refused if it is retried after the entitlement is bought, rather than
        // being swallowed as a duplicate.
        if (!await _entitlements.IsEntitledAsync(store.CustomerId, LogShippingFeatureSlug, cancellationToken))
        {
            throw new ForbiddenException("This store's customer is not entitled to log shipping.");
        }

        if (!await ClaimAsync(store, "logs", idempotencyKey, cancellationToken))
        {
            return IngestionReceipt.DuplicateBatch;
        }

        var now = _clock.UtcNow;
        var accepted = new List<StoreLogEntry>(entries.Count);
        var errors = new List<string>();

        foreach (var input in entries)
        {
            try
            {
                accepted.Add(StoreLogEntry.Record(
                    Guid.NewGuid(),
                    store.StoreId,
                    store.CustomerId,
                    input.Timestamp,
                    now,
                    input.Level,
                    store.Environment,
                    input.Message,
                    input.Service,
                    storeVersion,
                    input.RequestId,
                    input.TraceId,
                    input.Exception,
                    input.AttributesJson));
            }
            catch (DomainException exception)
            {
                errors.Add(exception.Message);
            }
        }

        if (accepted.Count > 0)
        {
            await _repository.AddLogsAsync(accepted, cancellationToken);
            await _repository.SaveChangesAsync(cancellationToken);
        }

        return new IngestionReceipt(accepted.Count, errors.Count, false, errors);
    }

    public Task<(IReadOnlyCollection<StoreErrorEvent> Items, long TotalCount)> ListErrorsAsync(
        Guid? storeId,
        int page,
        int pageSize,
        CancellationToken cancellationToken) =>
        _repository.ListErrorsAsync(storeId, Page(page), Size(pageSize), cancellationToken);

    public Task<(IReadOnlyCollection<StoreLifecycleEvent> Items, long TotalCount)> ListEventsAsync(
        Guid? storeId,
        int page,
        int pageSize,
        CancellationToken cancellationToken) =>
        _repository.ListEventsAsync(storeId, Page(page), Size(pageSize), cancellationToken);

    /// <summary>
    /// The environment in the payload and the one the token was minted for must
    /// agree. They can only diverge if a store is misconfigured or a token is
    /// being used by something else, and both are worth refusing loudly
    /// (docs/api-contracts.md §3).
    /// </summary>
    private static void RequireMatchingEnvironment(IngestingStore store, string payloadEnvironment)
    {
        if (!string.Equals(store.Environment, payloadEnvironment?.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            throw new ValidationException(new Dictionary<string, string[]>
            {
                ["environment"] = [$"This store is registered as '{store.Environment}'."],
            });
        }
    }

    private static void RequireWithinCap(int count, int cap, string field)
    {
        if (count == 0)
        {
            throw new ValidationException(new Dictionary<string, string[]>
            {
                [field] = ["A batch must carry at least one item."],
            });
        }

        // Refused rather than truncated: silently dropping the tail of a batch
        // would make a store believe it reported something it did not.
        if (count > cap)
        {
            throw new ValidationException(new Dictionary<string, string[]>
            {
                [field] = [$"A batch may carry at most {cap} items."],
            });
        }
    }

    /// <summary>
    /// True when this batch has not been seen before. A batch with no key is
    /// always written: idempotency is the caller's to ask for, and inventing a
    /// key from the payload would make two genuinely identical crashes collapse
    /// into one.
    /// </summary>
    private async Task<bool> ClaimAsync(IngestingStore store, string surface, string? idempotencyKey, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(idempotencyKey))
        {
            return true;
        }

        var claimed = await _replay.TryConsumeAsync(
            $"ingest:{surface}:{store.StoreId}",
            idempotencyKey.Trim(),
            _options.IdempotencyWindow,
            cancellationToken);

        if (!claimed)
        {
            _logger.LogInformation(
                "Ignored a replayed {Surface} batch from store {StoreId}",
                surface,
                store.StoreId);
        }

        return claimed;
    }

    private static int Page(int page) => page < 1 ? 1 : page;

    private static int Size(int pageSize) => pageSize is < 1 or > MaxPageSize ? 25 : pageSize;
}
