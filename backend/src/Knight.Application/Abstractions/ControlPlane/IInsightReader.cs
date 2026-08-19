namespace Knight.Application.Abstractions.ControlPlane;

/// <summary>
/// The read models behind the dashboard's summary panels.
///
/// Each of these spans several modules — a customer's activity is its audit
/// trail, a store's usage is its error and log volume plus its probe latency,
/// the entitlement matrix is plans crossed with features — so none of them
/// belongs to a single module and all of them are projections rather than
/// aggregates. They live behind one port for that reason, and are read-only by
/// construction: there is no write path here to get wrong.
///
/// Everything reported is something KNIGHT actually measures. Where a figure
/// would have to be invented — a store's request rate, its disk usage — the
/// field is absent rather than estimated, and the screen says so.
/// </summary>
public interface IInsightReader
{
    /// <summary>The platform's own dependencies and their state, for the infrastructure screen.</summary>
    Task<IReadOnlyCollection<PlatformServiceStatus>> ReadServicesAsync(CancellationToken cancellationToken);

    /// <summary>
    /// The reports KNIGHT can actually produce, each with the real time of the
    /// most recent data behind it. A catalogue entry whose data does not exist
    /// yet reports a null timestamp rather than a plausible-looking one.
    /// </summary>
    Task<IReadOnlyCollection<ReportSummary>> ReadReportsAsync(CancellationToken cancellationToken);

    /// <summary>Which plan grants which feature, as the plans screen shows it.</summary>
    Task<IReadOnlyCollection<EntitlementMatrixRow>> ReadEntitlementMatrixAsync(CancellationToken cancellationToken);

    /// <summary>One customer's recent activity, drawn from the audit trail.</summary>
    Task<IReadOnlyCollection<ActivityItem>> ReadCustomerActivityAsync(
        Guid customerId,
        int limit,
        CancellationToken cancellationToken);

    /// <summary>What a store has been doing, hour by hour, over the window.</summary>
    Task<StoreUsage?> ReadStoreUsageAsync(Guid storeId, int hours, CancellationToken cancellationToken);
}

/// <summary>
/// One platform dependency. <paramref name="Metrics"/> are label/value pairs
/// shown beside it — deliberately strings, because what is worth showing differs
/// per service and a typed shape would fit none of them.
/// </summary>
public sealed record PlatformServiceStatus(
    string Key,
    string Name,
    string Detail,
    string Status,
    IReadOnlyCollection<KeyValuePair<string, string>> Metrics);

public sealed record ReportSummary(string Key, string Name, string Description, DateTimeOffset? UpdatedAt);

/// <summary>
/// One feature and what each plan grants for it. <paramref name="Values"/> is
/// keyed by plan key; a missing entry means the plan does not include it.
/// </summary>
public sealed record EntitlementMatrixRow(
    string FeatureSlug,
    string FeatureName,
    IReadOnlyDictionary<string, string> Values);

public sealed record ActivityItem(
    Guid Id,
    DateTimeOffset OccurredAt,
    string Kind,
    string Title,
    string Actor);

/// <summary>
/// A store's measured activity.
///
/// There is deliberately no request count and no storage figure: stores do not
/// report either, and a dashboard that showed an invented number would be worse
/// than one that shows fewer real ones.
/// </summary>
public sealed record StoreUsage(
    IReadOnlyList<int> Errors,
    IReadOnlyList<int> Logs,
    IReadOnlyList<int> HealthLatencyMs,
    int WindowHours,
    long TotalErrors,
    long TotalLogs);
