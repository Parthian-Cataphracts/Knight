using Knight.Domain.Common;
using Knight.Domain.Exceptions;

namespace Stores.Domain;

/// <summary>
/// One observation of a store's health: what KNIGHT saw when it polled, or what
/// the store said when it sent a heartbeat (docs/domain-model.md section 7).
///
/// The table is append-only. It is the evidence behind the store's integration
/// status, and a status that changed for a reason nobody can look up afterwards
/// is not much better than no status at all. Retention is a phase 7 job, not a
/// reason to overwrite rows here.
///
/// Dependency detail arrives as the store's own JSON and is stored as given.
/// KNIGHT deliberately does not model a store's dependencies: it is not that
/// store's backend, and the set differs per store and per feature
/// (docs/README.md rule 1).
/// </summary>
public sealed class StoreHealthCheck : Entity, ICustomerOwned
{
    /// <summary>A store's answer is not evidence about anything else; oversized payloads are truncated rather than trusted.</summary>
    public const int MaxPayloadLength = 8000;

    public Guid StoreId { get; private set; }

    public Guid CustomerId { get; private set; }

    public DateTimeOffset CheckedAt { get; private set; }

    public StoreHealthStatus Status { get; private set; }

    public HealthCheckSource Source { get; private set; }

    public bool IsHealthy => Status is StoreHealthStatus.Healthy;

    /// <summary>Null when the store never answered.</summary>
    public int? ResponseTimeMs { get; private set; }

    public string? ReportedVersion { get; private set; }

    /// <summary>The store's own dependency block, verbatim JSON.</summary>
    public string? Dependencies { get; private set; }

    /// <summary>Feature slugs the store reports as installed, verbatim JSON.</summary>
    public string? ReportedFeatures { get; private set; }

    /// <summary>Why the observation is not healthy, in one line. Never a stack trace or a URL with credentials in it.</summary>
    public string? Detail { get; private set; }

    private StoreHealthCheck()
    {
    }

    private StoreHealthCheck(
        Guid id,
        Guid storeId,
        Guid customerId,
        DateTimeOffset checkedAt,
        StoreHealthStatus status,
        HealthCheckSource source,
        int? responseTimeMs,
        string? reportedVersion,
        string? dependencies,
        string? reportedFeatures,
        string? detail)
        : base(id)
    {
        StoreId = storeId;
        CustomerId = customerId;
        CheckedAt = checkedAt;
        Status = status;
        Source = source;
        ResponseTimeMs = responseTimeMs;
        ReportedVersion = reportedVersion;
        Dependencies = dependencies;
        ReportedFeatures = reportedFeatures;
        Detail = detail;
    }

    public static StoreHealthCheck Record(
        Guid id,
        Guid storeId,
        Guid customerId,
        DateTimeOffset checkedAt,
        StoreHealthStatus status,
        HealthCheckSource source,
        int? responseTimeMs = null,
        string? reportedVersion = null,
        string? dependencies = null,
        string? reportedFeatures = null,
        string? detail = null)
    {
        if (storeId == Guid.Empty)
        {
            throw DomainException.Validation("A health check must belong to a store.");
        }

        if (responseTimeMs is < 0)
        {
            throw DomainException.Validation("A response time cannot be negative.");
        }

        return new StoreHealthCheck(
            id,
            storeId,
            customerId,
            checkedAt,
            status,
            source,
            responseTimeMs,
            StoreNormalization.NormalizeVersion(reportedVersion),
            Truncate(dependencies),
            Truncate(reportedFeatures),
            Truncate(detail, 500));
    }

    private static string? Truncate(string? value, int max = MaxPayloadLength) =>
        string.IsNullOrWhiteSpace(value)
            ? null
            : value.Length <= max ? value : value[..max];
}
