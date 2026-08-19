using System.Diagnostics.Metrics;
using Knight.Application.Abstractions.Observability;
using Microsoft.Extensions.Logging;

namespace Knight.Infrastructure.Telemetry;

/// <summary>
/// KNIGHT's own instruments (docs/observability.md §3).
///
/// Built on <c>System.Diagnostics.Metrics</c>, which is in the base library, so
/// the modules that record measurements depend on nothing but a port and a host
/// with no exporter configured pays almost nothing. Attaching OpenTelemetry
/// later — or a different collector entirely — is a hosting decision rather than
/// a code change.
///
/// Every method here is total and swallows nothing it should not: recording a
/// measurement cannot throw in practice, and if it ever did, failing the
/// operation being measured would be a strictly worse outcome than losing the
/// measurement.
/// </summary>
public sealed class KnightMetrics : IKnightMetrics, IDisposable
{
    /// <summary>The meter name a collector subscribes to. Part of the operational contract; changing it silences dashboards.</summary>
    public const string MeterName = "Knight.ControlPlane";

    private readonly Meter _meter;
    private readonly ILogger<KnightMetrics> _logger;

    private readonly Counter<long> _ingestAccepted;
    private readonly Counter<long> _ingestRejected;
    private readonly Histogram<double> _storeHealthDuration;
    private readonly Counter<long> _errorGroupsCreated;
    private readonly Counter<long> _errorGroupsRegressed;
    private readonly Histogram<double> _jobDuration;
    private readonly Counter<long> _jobsFailed;
    private readonly Counter<long> _rollbacks;
    private readonly Counter<long> _notifications;
    private readonly Counter<long> _alertsRaised;

    private IObservabilityGaugeSource? _gauges;

    /// <summary>
    /// The last snapshot read, and when. Gauges are observed on the scrape path,
    /// and a scrape must not become a burst of database queries — one snapshot
    /// serves every gauge in a scrape, and is reused for a short window so two
    /// collectors do not double the load.
    /// </summary>
    private ObservabilitySnapshot _snapshot = Empty;
    private DateTimeOffset _snapshotTakenAt = DateTimeOffset.MinValue;
    private readonly TimeSpan _snapshotTtl = TimeSpan.FromSeconds(10);
    private readonly SemaphoreSlim _snapshotLock = new(1, 1);

    private static readonly ObservabilitySnapshot Empty = new(0, 0, 0, 0, 0, 0, 0, 0, 0, 0);

    public KnightMetrics(IMeterFactory factory, ILogger<KnightMetrics> logger)
    {
        _meter = factory.Create(MeterName);
        _logger = logger;

        _ingestAccepted = _meter.CreateCounter<long>(
            "knight.ingest.events",
            unit: "{event}",
            description: "Telemetry items accepted from stores.");

        _ingestRejected = _meter.CreateCounter<long>(
            "knight.ingest.rejected",
            unit: "{event}",
            description: "Telemetry items refused as malformed. A rising rate means a store is shipping something KNIGHT cannot read.");

        _storeHealthDuration = _meter.CreateHistogram<double>(
            "knight.store.health.check.duration",
            unit: "ms",
            description: "How long a store took to answer its health probe.");

        _errorGroupsCreated = _meter.CreateCounter<long>(
            "knight.errors.groups.created",
            unit: "{group}",
            description: "New error groups. The rate at which stores are finding new ways to fail, not how often they fail.");

        _errorGroupsRegressed = _meter.CreateCounter<long>(
            "knight.errors.groups.regressed",
            unit: "{group}",
            description: "Resolved problems that came back. A fix that did not hold.");

        _jobDuration = _meter.CreateHistogram<double>(
            "knight.jobs.duration",
            unit: "s",
            description: "Installation job duration by type and outcome.");

        _jobsFailed = _meter.CreateCounter<long>(
            "knight.jobs.failed",
            unit: "{job}",
            description: "Failed job steps, by the error code an operator would search for.");

        _rollbacks = _meter.CreateCounter<long>(
            "knight.feature.rollbacks",
            unit: "{rollback}",
            description: "Rollbacks by outcome. The share needing manual intervention is the number that matters.");

        _notifications = _meter.CreateCounter<long>(
            "knight.notifications.delivered",
            unit: "{notification}",
            description: "Notification delivery attempts by channel kind and outcome.");

        _alertsRaised = _meter.CreateCounter<long>(
            "knight.alerts.raised",
            unit: "{alert}",
            description: "Newly raised alerts. Re-observations of an open alert are excluded: they are not new information.");
    }

    public void IngestAccepted(string kind, int accepted, int rejected)
    {
        var tag = new KeyValuePair<string, object?>("kind", kind);

        if (accepted > 0)
        {
            _ingestAccepted.Add(accepted, tag);
        }

        if (rejected > 0)
        {
            _ingestRejected.Add(rejected, tag);
        }
    }

    public void StoreHealthChecked(string outcome, double milliseconds) =>
        _storeHealthDuration.Record(milliseconds, new KeyValuePair<string, object?>("outcome", outcome));

    public void ErrorGroupCreated(string environment) =>
        _errorGroupsCreated.Add(1, new KeyValuePair<string, object?>("environment", environment));

    public void ErrorGroupRegressed() => _errorGroupsRegressed.Add(1);

    public void JobCompleted(string jobType, string outcome, double seconds) =>
        _jobDuration.Record(
            seconds,
            new KeyValuePair<string, object?>("type", jobType),
            new KeyValuePair<string, object?>("outcome", outcome));

    public void JobStepFailed(string jobType, string step, string errorCode) =>
        _jobsFailed.Add(
            1,
            new KeyValuePair<string, object?>("type", jobType),
            new KeyValuePair<string, object?>("step", step),
            new KeyValuePair<string, object?>("errorCode", errorCode));

    public void RollbackCompleted(string outcome) =>
        _rollbacks.Add(1, new KeyValuePair<string, object?>("outcome", outcome));

    public void NotificationDelivered(string channelKind, string outcome) =>
        _notifications.Add(
            1,
            new KeyValuePair<string, object?>("channel", channelKind),
            new KeyValuePair<string, object?>("outcome", outcome));

    public void AlertRaised(string ruleKey, string severity) =>
        _alertsRaised.Add(
            1,
            new KeyValuePair<string, object?>("rule", ruleKey),
            new KeyValuePair<string, object?>("severity", severity));

    /// <summary>
    /// Registers the observable gauges. Called once at startup; calling it again
    /// replaces the source rather than adding a second set of instruments, which
    /// would double every reported value.
    /// </summary>
    public void RegisterGauges(IObservabilityGaugeSource source)
    {
        if (_gauges is not null)
        {
            _gauges = source;

            return;
        }

        _gauges = source;

        _meter.CreateObservableGauge(
            "knight.incidents.open",
            () => new[]
            {
                new Measurement<int>(Snapshot().OpenIncidents, new KeyValuePair<string, object?>("severity", "all")),
                new Measurement<int>(Snapshot().CriticalOpenIncidents, new KeyValuePair<string, object?>("severity", "critical")),
            },
            description: "Incidents that are open right now.");

        _meter.CreateObservableGauge(
            "knight.jobs.pending",
            () => new[]
            {
                new Measurement<int>(Snapshot().QueuedJobs, new KeyValuePair<string, object?>("state", "queued")),
                new Measurement<int>(Snapshot().RunningJobs, new KeyValuePair<string, object?>("state", "running")),
            },
            description: "Installation jobs waiting or in flight. A queue that only grows means agents have stopped claiming.");

        _meter.CreateObservableGauge(
            "knight.notifications.pending",
            () => Snapshot().PendingNotifications,
            description: "Notifications queued but not yet delivered.");

        _meter.CreateObservableGauge(
            "knight.alerts.open",
            () => Snapshot().OpenAlerts,
            description: "Alerts whose condition is still true.");

        _meter.CreateObservableGauge(
            "knight.feature.installations",
            () => new[]
            {
                new Measurement<int>(Snapshot().InstalledFeatures, new KeyValuePair<string, object?>("state", "installed")),
                new Measurement<int>(Snapshot().FailedInstallations, new KeyValuePair<string, object?>("state", "failed")),
            },
            description: "Feature installations by state across the fleet.");

        _meter.CreateObservableGauge(
            "knight.stores.connected",
            () => Snapshot().StoresConnected,
            description: "Stores currently reporting as connected.");

        _meter.CreateObservableGauge(
            "knight.servers.offline",
            () => Snapshot().ServersOffline,
            description: "Machines the fleet sweep has decided are offline.");
    }

    /// <summary>
    /// The current gauge values, refreshed at most once per TTL.
    ///
    /// Blocking here is deliberate and bounded: the metrics API's observable
    /// callbacks are synchronous, and a scrape that silently reported stale
    /// zeroes would be worse than one that waits a few milliseconds. A failure to
    /// read returns the last known values rather than zeroes — a database blip
    /// must not look like "no open incidents".
    /// </summary>
    private ObservabilitySnapshot Snapshot()
    {
        if (_gauges is null || DateTimeOffset.UtcNow - _snapshotTakenAt < _snapshotTtl)
        {
            return _snapshot;
        }

        if (!_snapshotLock.Wait(TimeSpan.Zero))
        {
            return _snapshot;
        }

        try
        {
            if (DateTimeOffset.UtcNow - _snapshotTakenAt < _snapshotTtl)
            {
                return _snapshot;
            }

            _snapshot = _gauges.ReadAsync(CancellationToken.None).GetAwaiter().GetResult();
            _snapshotTakenAt = DateTimeOffset.UtcNow;
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Could not read observability gauges; the previous values stand.");

            // Deliberately not resetting the timestamp: the next scrape retries
            // rather than serving a stale value for a full TTL after a failure.
        }
        finally
        {
            _snapshotLock.Release();
        }

        return _snapshot;
    }

    public void Dispose()
    {
        _meter.Dispose();
        _snapshotLock.Dispose();
    }
}

/// <summary>
/// What a host with no metrics configured uses, and what the tests use.
///
/// Recording a measurement must never be a reason for an operation to behave
/// differently, so the null implementation does nothing at all rather than
/// buffering.
/// </summary>
public sealed class NullKnightMetrics : IKnightMetrics
{
    public void IngestAccepted(string kind, int accepted, int rejected)
    {
    }

    public void StoreHealthChecked(string outcome, double milliseconds)
    {
    }

    public void ErrorGroupCreated(string environment)
    {
    }

    public void ErrorGroupRegressed()
    {
    }

    public void JobCompleted(string jobType, string outcome, double seconds)
    {
    }

    public void JobStepFailed(string jobType, string step, string errorCode)
    {
    }

    public void RollbackCompleted(string outcome)
    {
    }

    public void NotificationDelivered(string channelKind, string outcome)
    {
    }

    public void AlertRaised(string ruleKey, string severity)
    {
    }

    public void RegisterGauges(IObservabilityGaugeSource source)
    {
    }
}
