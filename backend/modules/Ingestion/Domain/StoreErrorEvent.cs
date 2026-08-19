using Knight.Domain.Common;
using Knight.Domain.Exceptions;

namespace Ingestion.Domain;

/// <summary>
/// One unhandled exception a store reported (docs/domain-model.md section 8).
///
/// Stored as the store sent it, minus anything too long to be worth keeping.
/// Grouping into <c>ErrorGroup</c>s by fingerprint is phase 5's job
/// ([`adr/0013`](../../../docs/adr/0013-error-grouping-strategy.md)); until then
/// <see cref="ErrorGroupId"/> stays null and these rows are the raw record.
///
/// Everything here originates outside KNIGHT and is treated accordingly: fields
/// are length-capped on the way in, and nothing is ever interpolated into a
/// query, a log format string or a page without escaping.
/// </summary>
public sealed class StoreErrorEvent : Entity, ICustomerOwned
{
    public const int MaxMessageLength = 2000;
    public const int MaxStackTraceLength = 20000;
    public const int MaxContextLength = 8000;

    public Guid StoreId { get; private set; }

    public Guid CustomerId { get; private set; }

    /// <summary>When the store says it happened. Not trusted for ordering — see <see cref="ReceivedAt"/>.</summary>
    public DateTimeOffset OccurredAt { get; private set; }

    /// <summary>When KNIGHT accepted it. The only timestamp KNIGHT itself can vouch for.</summary>
    public DateTimeOffset ReceivedAt { get; private set; }

    public string Environment { get; private set; }

    public string? StoreVersion { get; private set; }

    public string ExceptionType { get; private set; }

    public string Message { get; private set; }

    public string? Endpoint { get; private set; }

    public string? HttpMethod { get; private set; }

    public int? StatusCode { get; private set; }

    public string? StackTrace { get; private set; }

    public string? RequestId { get; private set; }

    public string? TraceId { get; private set; }

    /// <summary>The store's own context bag, verbatim JSON, already scrubbed store-side.</summary>
    public string? Context { get; private set; }

    /// <summary>Assigned by error grouping in phase 5; null for everything ingested before it exists.</summary>
    public Guid? ErrorGroupId { get; private set; }

    private StoreErrorEvent()
    {
        Environment = string.Empty;
        ExceptionType = string.Empty;
        Message = string.Empty;
    }

    private StoreErrorEvent(
        Guid id,
        Guid storeId,
        Guid customerId,
        DateTimeOffset occurredAt,
        DateTimeOffset receivedAt,
        string environment,
        string? storeVersion,
        string exceptionType,
        string message,
        string? endpoint,
        string? httpMethod,
        int? statusCode,
        string? stackTrace,
        string? requestId,
        string? traceId,
        string? context)
        : base(id)
    {
        StoreId = storeId;
        CustomerId = customerId;
        OccurredAt = occurredAt;
        ReceivedAt = receivedAt;
        Environment = environment;
        StoreVersion = storeVersion;
        ExceptionType = exceptionType;
        Message = message;
        Endpoint = endpoint;
        HttpMethod = httpMethod;
        StatusCode = statusCode;
        StackTrace = stackTrace;
        RequestId = requestId;
        TraceId = traceId;
        Context = context;
    }

    public static StoreErrorEvent Record(
        Guid id,
        Guid storeId,
        Guid customerId,
        DateTimeOffset occurredAt,
        DateTimeOffset receivedAt,
        string environment,
        string? storeVersion,
        string? exceptionType,
        string? message,
        string? endpoint = null,
        string? httpMethod = null,
        int? statusCode = null,
        string? stackTrace = null,
        string? requestId = null,
        string? traceId = null,
        string? context = null)
    {
        if (storeId == Guid.Empty)
        {
            throw DomainException.Validation("An error event must belong to a store.");
        }

        if (string.IsNullOrWhiteSpace(exceptionType))
        {
            throw DomainException.Validation("An error event must name an exception type.");
        }

        if (string.IsNullOrWhiteSpace(message))
        {
            throw DomainException.Validation("An error event must carry a message.");
        }

        // A store clock running ahead would otherwise put events in the future,
        // where every "last hour" query misses them until real time catches up.
        var occurred = occurredAt > receivedAt ? receivedAt : occurredAt;

        return new StoreErrorEvent(
            id,
            storeId,
            customerId,
            occurred,
            receivedAt,
            IngestionText.Require(environment, 20, "environment"),
            IngestionText.Clip(storeVersion, 50),
            IngestionText.Require(exceptionType, 200, "exceptionType"),
            IngestionText.Clip(message, MaxMessageLength)!,
            IngestionText.Clip(endpoint, 500),
            IngestionText.Clip(httpMethod, 10),
            statusCode,
            IngestionText.Clip(stackTrace, MaxStackTraceLength),
            IngestionText.Clip(requestId, 100),
            IngestionText.Clip(traceId, 100),
            IngestionText.Clip(context, MaxContextLength));
    }
}
