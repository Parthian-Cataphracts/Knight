using Knight.Domain.Common;
using Knight.Domain.Exceptions;

namespace Ingestion.Domain;

public enum StoreEventSeverity
{
    Info = 0,
    Warning = 1,
    Error = 2,
}

/// <summary>
/// Something a store did that KNIGHT should know about but did not cause: a
/// deployment finished, a backup ran, a scheduled job failed.
///
/// The type is a free string on purpose. KNIGHT does not own the list of things
/// a store can do — that would make it the store's backend by the back door —
/// so it records the type as sent and interprets only the handful it acts on
/// (docs/README.md rule 1). An unrecognised type is stored, not refused.
/// </summary>
public sealed class StoreLifecycleEvent : Entity, ICustomerOwned
{
    public const int MaxPayloadLength = 8000;

    /// <summary>Deployment reports are the one type KNIGHT reacts to, by recording a <c>StoreDeployment</c>.</summary>
    public const string DeploymentCompletedType = "deployment.completed";

    public const string DeploymentFailedType = "deployment.failed";

    public Guid StoreId { get; private set; }

    public Guid CustomerId { get; private set; }

    public DateTimeOffset OccurredAt { get; private set; }

    public DateTimeOffset ReceivedAt { get; private set; }

    public string Type { get; private set; }

    public StoreEventSeverity Severity { get; private set; }

    public string Summary { get; private set; }

    public string Environment { get; private set; }

    public string? TraceId { get; private set; }

    /// <summary>The event's own detail, verbatim JSON.</summary>
    public string? Payload { get; private set; }

    private StoreLifecycleEvent()
    {
        Type = string.Empty;
        Summary = string.Empty;
        Environment = string.Empty;
    }

    private StoreLifecycleEvent(
        Guid id,
        Guid storeId,
        Guid customerId,
        DateTimeOffset occurredAt,
        DateTimeOffset receivedAt,
        string type,
        StoreEventSeverity severity,
        string summary,
        string environment,
        string? traceId,
        string? payload)
        : base(id)
    {
        StoreId = storeId;
        CustomerId = customerId;
        OccurredAt = occurredAt;
        ReceivedAt = receivedAt;
        Type = type;
        Severity = severity;
        Summary = summary;
        Environment = environment;
        TraceId = traceId;
        Payload = payload;
    }

    public static StoreLifecycleEvent Record(
        Guid id,
        Guid storeId,
        Guid customerId,
        DateTimeOffset occurredAt,
        DateTimeOffset receivedAt,
        string? type,
        StoreEventSeverity severity,
        string? summary,
        string environment,
        string? traceId = null,
        string? payload = null)
    {
        if (storeId == Guid.Empty)
        {
            throw DomainException.Validation("An event must belong to a store.");
        }

        return new StoreLifecycleEvent(
            id,
            storeId,
            customerId,
            occurredAt > receivedAt ? receivedAt : occurredAt,
            receivedAt,
            IngestionText.Require(type, 100, "type").ToLowerInvariant(),
            severity,
            IngestionText.Clip(summary, 500) ?? IngestionText.Require(type, 100, "type"),
            IngestionText.Require(environment, 20, "environment"),
            IngestionText.Clip(traceId, 100),
            IngestionText.Clip(payload, MaxPayloadLength));
    }
}
