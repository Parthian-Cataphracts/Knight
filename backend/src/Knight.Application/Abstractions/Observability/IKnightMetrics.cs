namespace Knight.Application.Abstractions.Observability;

/// <summary>
/// KNIGHT's own metrics (docs/observability.md §3).
///
/// These are about **KNIGHT**, not about the stores it manages. A store's error
/// rate is product data and lives in `error_groups`; the number of error groups
/// KNIGHT created per second is operational data about KNIGHT and lives here.
/// Confusing the two is how a monitoring system ends up unable to answer "is the
/// control plane healthy?" during an incident affecting the things it monitors.
///
/// Declared as a port so modules can record without depending on a telemetry
/// SDK, and so a host with no exporter configured pays nothing. Every method is
/// deliberately total and non-throwing: a metric that fails must never fail the
/// operation it was measuring.
/// </summary>
public interface IKnightMetrics
{
    /// <summary>A batch of telemetry arrived from a store.</summary>
    void IngestAccepted(string kind, int accepted, int rejected);

    /// <summary>A store's health was probed. Duration in milliseconds, with the outcome.</summary>
    void StoreHealthChecked(string outcome, double milliseconds);

    /// <summary>A new error group was created — the rate at which stores are finding new ways to fail.</summary>
    void ErrorGroupCreated(string environment);

    /// <summary>An error group recurred after being resolved.</summary>
    void ErrorGroupRegressed();

    /// <summary>An installation job finished. Duration in seconds, with type and outcome.</summary>
    void JobCompleted(string jobType, string outcome, double seconds);

    /// <summary>A job step failed, keyed by the code an operator would search for.</summary>
    void JobStepFailed(string jobType, string step, string errorCode);

    /// <summary>A rollback finished, by outcome — the figure that says whether failures are self-healing.</summary>
    void RollbackCompleted(string outcome);

    /// <summary>A notification delivery attempt finished.</summary>
    void NotificationDelivered(string channelKind, string outcome);

    /// <summary>An alert was raised. Re-observations of an open alert are not counted: they are not new information.</summary>
    void AlertRaised(string ruleKey, string severity);

    /// <summary>
    /// Registers the callbacks behind the gauges — open incidents, queued and
    /// running jobs, installations by state.
    ///
    /// Gauges are pull-based rather than pushed, because their value is a
    /// property of the database at the moment of scraping, and a pushed gauge
    /// goes stale silently the moment whatever was pushing it stops.
    /// </summary>
    void RegisterGauges(IObservabilityGaugeSource source);
}

/// <summary>
/// Supplies the current value of each gauge when the metrics system asks.
///
/// Implemented over the database. It is called on the scrape path, so every
/// query behind it must be a cheap count against an index — a gauge that is
/// expensive to read becomes load that arrives exactly when the system is
/// already struggling.
/// </summary>
public interface IObservabilityGaugeSource
{
    Task<ObservabilitySnapshot> ReadAsync(CancellationToken cancellationToken);
}

/// <summary>The gauge values at one instant.</summary>
public sealed record ObservabilitySnapshot(
    int OpenIncidents,
    int CriticalOpenIncidents,
    int QueuedJobs,
    int RunningJobs,
    int PendingNotifications,
    int OpenAlerts,
    int InstalledFeatures,
    int FailedInstallations,
    int StoresConnected,
    int ServersOffline);
