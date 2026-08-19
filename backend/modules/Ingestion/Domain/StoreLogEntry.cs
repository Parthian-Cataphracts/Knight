using Knight.Application.Abstractions.Observability;
using Knight.Domain.Common;
using Knight.Domain.Exceptions;

namespace Ingestion.Domain;

/// <summary>
/// A structured log line shipped from a store (docs/domain-model.md section 9).
///
/// Log shipping is entitlement-gated: it is a paid capability, and a store whose
/// customer is not entitled to it is refused rather than quietly billed for
/// storage nobody agreed to (docs/api-contracts.md §3). The gate is enforced
/// where the entitlement is known — in the ingestion service — never here.
/// </summary>
public sealed class StoreLogEntry : Entity, ICustomerOwned
{
    public const int MaxMessageLength = 4000;
    public const int MaxAttributesLength = 4000;

    public Guid StoreId { get; private set; }

    public Guid CustomerId { get; private set; }

    public DateTimeOffset Timestamp { get; private set; }

    public DateTimeOffset ReceivedAt { get; private set; }

    public string Level { get; private set; }

    public string? Service { get; private set; }

    public string Environment { get; private set; }

    public string? StoreVersion { get; private set; }

    public string? RequestId { get; private set; }

    public string? TraceId { get; private set; }

    public string Message { get; private set; }

    public string? Exception { get; private set; }

    public string? Attributes { get; private set; }

    private StoreLogEntry()
    {
        Level = string.Empty;
        Environment = string.Empty;
        Message = string.Empty;
    }

    private StoreLogEntry(
        Guid id,
        Guid storeId,
        Guid customerId,
        DateTimeOffset timestamp,
        DateTimeOffset receivedAt,
        string level,
        string? service,
        string environment,
        string? storeVersion,
        string? requestId,
        string? traceId,
        string message,
        string? exception,
        string? attributes)
        : base(id)
    {
        StoreId = storeId;
        CustomerId = customerId;
        Timestamp = timestamp;
        ReceivedAt = receivedAt;
        Level = level;
        Service = service;
        Environment = environment;
        StoreVersion = storeVersion;
        RequestId = requestId;
        TraceId = traceId;
        Message = message;
        Exception = exception;
        Attributes = attributes;
    }

    public static StoreLogEntry Record(
        Guid id,
        Guid storeId,
        Guid customerId,
        DateTimeOffset timestamp,
        DateTimeOffset receivedAt,
        string? level,
        string environment,
        string? message,
        string? service = null,
        string? storeVersion = null,
        string? requestId = null,
        string? traceId = null,
        string? exception = null,
        string? attributes = null)
    {
        if (storeId == Guid.Empty)
        {
            throw DomainException.Validation("A log entry must belong to a store.");
        }

        return new StoreLogEntry(
            id,
            storeId,
            customerId,
            timestamp > receivedAt ? receivedAt : timestamp,
            receivedAt,
            IngestionText.Require(level, 20, "level").ToUpperInvariant(),
            IngestionText.Clip(service, 100),
            IngestionText.Require(environment, 20, "environment"),
            IngestionText.Clip(storeVersion, 50),
            IngestionText.Clip(requestId, 100),
            IngestionText.Clip(traceId, 100),
            // Redacted on the way in, not on the way out. A log line is the
            // least structured thing KNIGHT stores and the most likely to carry
            // a credential in a field nobody named; storing it and redacting at
            // read time would mean the secret is in the database and the backups
            // regardless of what any screen shows.
            Redaction.Text(IngestionText.Clip(message, MaxMessageLength))
                ?? throw DomainException.Validation("A log entry must carry a message."),
            Redaction.Text(IngestionText.Clip(exception, StoreErrorEvent.MaxStackTraceLength)),
            Redaction.Json(IngestionText.Clip(attributes, MaxAttributesLength)));
    }
}
