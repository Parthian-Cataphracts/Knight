using Servers.Domain;

namespace Servers;

public sealed record AlertPage(IReadOnlyCollection<Alert> Items, int Page, int PageSize, long TotalCount);

/// <summary>
/// What the dashboard's front page needs, in one read.
///
/// Assembled server-side rather than by the browser making eight calls: the
/// overview is the screen an operator leaves open, and eight polling requests per
/// refresh is how a monitoring page becomes the thing generating the load.
/// </summary>
public sealed record MonitoringOverview(
    int TotalServers,
    int HealthyServers,
    int DegradedServers,
    int OfflineServers,
    int UnknownServers,
    int TotalAgents,
    int OnlineAgents,
    int OfflineAgents,
    int OpenAlerts,
    int CriticalAlerts,
    IReadOnlyCollection<Alert> RecentAlerts,
    IReadOnlyCollection<ServerSummary> Servers);

public sealed record ServerSummary(
    Guid Id,
    string Name,
    string Environment,
    string HostingModel,
    string Status,
    string? StatusReason,
    DateTimeOffset? LastSeenAt,
    double? CpuPercent,
    double? MemoryPercent,
    double? DiskPercent);

/// <summary>
/// Evaluating what the fleet's state means, and recording it.
///
/// The evaluation is separate from the reporting on purpose. An agent's heartbeat
/// says what one machine looks like; deciding that a machine is *offline* is a
/// judgement about absence, and absence cannot be reported by the thing that is
/// absent. Only a sweep that runs whether or not anybody checks in can make it.
/// </summary>
public interface IMonitoringService
{
    /// <summary>
    /// Looks for servers and agents that have gone quiet, moves them to offline
    /// and raises alerts. Returns how many changed.
    /// </summary>
    Task<int> EvaluateAsync(CancellationToken cancellationToken);

    /// <summary>Deletes metric samples past their retention window and answers how many went.</summary>
    Task<int> ApplyRetentionAsync(CancellationToken cancellationToken);

    Task<MonitoringOverview> GetOverviewAsync(CancellationToken cancellationToken);

    Task<AlertPage> ListAlertsAsync(
        int page,
        int pageSize,
        AlertSeverity? severity,
        AlertSource? source,
        bool openOnly,
        CancellationToken cancellationToken);

    Task<Alert> AcknowledgeAlertAsync(Guid alertId, CancellationToken cancellationToken);

    Task<Alert> ResolveAlertAsync(Guid alertId, CancellationToken cancellationToken);

    /// <summary>
    /// Raises an alert, or records another occurrence of one already open for the
    /// same rule and source. The deduplication is what keeps a six-hour outage
    /// one row rather than seven hundred.
    /// </summary>
    Task<Alert> RaiseAsync(
        AlertSource source,
        Guid sourceId,
        AlertSeverity severity,
        string ruleKey,
        string message,
        Guid? customerId,
        CancellationToken cancellationToken);
}
