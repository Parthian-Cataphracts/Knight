using Knight.Application.Abstractions.ControlPlane;
using Knight.Application.Abstractions.Identity;
using Knight.Application.Abstractions.Observability;
using Knight.Application.Abstractions.Time;
using Knight.Application.Exceptions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Servers.Domain;

namespace Servers;

/// <summary>
/// Evaluating the fleet, raising alerts, and answering the overview.
///
/// The evaluation is idempotent and safe to run as often as you like. It decides
/// what is true now and moves the record to match — it does not accumulate. That
/// matters because it runs on a timer: a sweep that raised a new alert every pass
/// would turn one broken machine into a pager storm.
/// </summary>
internal sealed class MonitoringService : IMonitoringService
{
    private const int MaxPageSize = 100;

    private readonly IServerRepository _servers;
    private readonly IAgentRepository _agents;
    private readonly IServerMetricRepository _metrics;
    private readonly IAlertRepository _alerts;
    private readonly IAuditTrail _audit;
    private readonly IDateTimeProvider _clock;
    private readonly ICurrentUser _currentUser;
    private readonly IAlertEventPublisher _alertEvents;
    private readonly ILogger<MonitoringService> _logger;
    private readonly IKnightMetrics _telemetry;
    private readonly ServerOptions _options;

    public MonitoringService(
        IServerRepository servers,
        IAgentRepository agents,
        IServerMetricRepository metrics,
        IAlertRepository alerts,
        IAuditTrail audit,
        IDateTimeProvider clock,
        ICurrentUser currentUser,
        IAlertEventPublisher alertEvents,
        ILogger<MonitoringService> logger,
        IKnightMetrics telemetry,
        IOptions<ServerOptions> options)
    {
        _servers = servers;
        _agents = agents;
        _metrics = metrics;
        _alerts = alerts;
        _audit = audit;
        _clock = clock;
        _currentUser = currentUser;
        _alertEvents = alertEvents;
        _logger = logger;
        _telemetry = telemetry;
        _options = options.Value;
    }

    public async Task<int> EvaluateAsync(CancellationToken cancellationToken)
    {
        var now = _clock.UtcNow;
        var changed = 0;

        // Collected rather than published inline: a recovery must not be
        // announced before the row that records it has been saved, or a
        // dispatcher racing this pass could send "resolved" for an alert the
        // database still shows as open.
        var resolved = new List<Alert>();

        foreach (var server in await _servers.ListReportingAsync(cancellationToken))
        {
            var overdue = server.IsOverdue(now, _options.HeartbeatInterval, _options.MissedIntervalsBeforeOffline);

            if (overdue && server.Status is not ServerStatus.Offline)
            {
                var silence = now - server.LastSeenAt!.Value;

                server.ApplyStatus(
                    ServerStatus.Offline,
                    $"No agent has reported for {Describe(silence)}.",
                    now);

                await RaiseAsync(
                    AlertSource.Server,
                    server.Id,
                    AlertSeverity.Critical,
                    AlertRules.ServerOffline,
                    $"{server.Name} has not reported for {Describe(silence)}.",
                    null,
                    cancellationToken);

                changed++;
                continue;
            }

            // Recovery closes the alert. A server that came back an hour ago and
            // still shows a red row is a monitoring system nobody trusts.
            if (!overdue && server.Status is ServerStatus.Healthy)
            {
                var open = await _alerts.FindOpenAsync(AlertRules.ServerOffline, server.Id, cancellationToken);
                if (open is not null)
                {
                    open.Resolve(now);
                    resolved.Add(open);
                    changed++;
                }
            }
        }

        foreach (var agent in await _agents.ListEnrolledAsync(cancellationToken))
        {
            if (agent.IsOverdue(now, _options.HeartbeatInterval, _options.MissedIntervalsBeforeOffline))
            {
                agent.MarkOffline(now);
                changed++;
            }
        }

        await _servers.SaveChangesAsync(cancellationToken);
        await _agents.SaveChangesAsync(cancellationToken);
        await _alerts.SaveChangesAsync(cancellationToken);

        foreach (var alert in resolved)
        {
            await PublishResolvedAsync(alert, cancellationToken);
        }

        return changed;
    }

    public async Task<int> ApplyRetentionAsync(CancellationToken cancellationToken)
    {
        var cutoff = _clock.UtcNow - _options.MetricRetention;
        var deleted = await _metrics.DeleteOlderThanAsync(cutoff, cancellationToken);

        if (deleted > 0)
        {
            await _audit.RecordAsync(
                "server.metrics.purged",
                "ServerMetric",
                null,
                null,
                cancellationToken,
                newValue: new { Cutoff = cutoff, Deleted = deleted });
        }

        return deleted;
    }

    public async Task<MonitoringOverview> GetOverviewAsync(CancellationToken cancellationToken)
    {
        var (servers, _) = await _servers.ListAsync(1, MaxPageSize, null, null, false, cancellationToken);
        var (alerts, _) = await _alerts.ListAsync(1, 10, null, null, true, cancellationToken);

        var summaries = new List<ServerSummary>(servers.Count);
        var totalAgents = 0;
        var onlineAgents = 0;
        var offlineAgents = 0;

        foreach (var server in servers)
        {
            var latest = await _metrics.GetLatestAsync(server.Id, cancellationToken);

            summaries.Add(new ServerSummary(
                server.Id,
                server.Name,
                server.Environment.ToString(),
                server.HostingModel.ToString(),
                server.Status.ToString(),
                server.StatusReason,
                server.LastSeenAt,
                latest?.CpuPercent,
                latest?.MemoryPercent,
                latest?.DiskPercent));

            foreach (var agent in await _agents.ListForServerAsync(server.Id, cancellationToken))
            {
                // Revoked agents are not counted. They are a record of something
                // that used to exist, not part of the fleet's current shape.
                if (agent.Status is AgentStatus.Revoked)
                {
                    continue;
                }

                totalAgents++;

                if (agent.Status is AgentStatus.Online)
                {
                    onlineAgents++;
                }
                else if (agent.Status is AgentStatus.Offline)
                {
                    offlineAgents++;
                }
            }
        }

        return new MonitoringOverview(
            servers.Count,
            servers.Count(server => server.Status is ServerStatus.Healthy),
            servers.Count(server => server.Status is ServerStatus.Degraded),
            servers.Count(server => server.Status is ServerStatus.Offline),
            servers.Count(server => server.Status is ServerStatus.Unknown),
            totalAgents,
            onlineAgents,
            offlineAgents,
            alerts.Count,
            alerts.Count(alert => alert.Severity is AlertSeverity.Critical),
            alerts,
            summaries);
    }

    public async Task<AlertPage> ListAlertsAsync(
        int page,
        int pageSize,
        AlertSeverity? severity,
        AlertSource? source,
        bool openOnly,
        CancellationToken cancellationToken)
    {
        var safePage = page < 1 ? 1 : page;
        var safeSize = pageSize is < 1 or > MaxPageSize ? 25 : pageSize;

        var (items, total) = await _alerts.ListAsync(safePage, safeSize, severity, source, openOnly, cancellationToken);
        return new AlertPage(items, safePage, safeSize, total);
    }

    public async Task<Alert> AcknowledgeAlertAsync(Guid alertId, CancellationToken cancellationToken)
    {
        var alert = await RequireAlertAsync(alertId, cancellationToken);

        alert.Acknowledge(_currentUser.UserId ?? Guid.Empty, _clock.UtcNow);
        await _alerts.SaveChangesAsync(cancellationToken);

        await _audit.RecordAsync(
            "alert.acknowledged", "Alert", alert.Id.ToString(), alert.CustomerId, cancellationToken);

        return alert;
    }

    public async Task<Alert> ResolveAlertAsync(Guid alertId, CancellationToken cancellationToken)
    {
        var alert = await RequireAlertAsync(alertId, cancellationToken);

        alert.Resolve(_clock.UtcNow);
        await _alerts.SaveChangesAsync(cancellationToken);

        await _audit.RecordAsync(
            "alert.resolved", "Alert", alert.Id.ToString(), alert.CustomerId, cancellationToken);

        await PublishResolvedAsync(alert, cancellationToken);

        return alert;
    }

    public async Task<Alert> RaiseAsync(
        AlertSource source,
        Guid sourceId,
        AlertSeverity severity,
        string ruleKey,
        string message,
        Guid? customerId,
        CancellationToken cancellationToken)
    {
        var now = _clock.UtcNow;
        var existing = await _alerts.FindOpenAsync(ruleKey, sourceId, cancellationToken);

        if (existing is not null)
        {
            existing.Observe(message, now);
            await _alerts.SaveChangesAsync(cancellationToken);

            // Published as not-new, so whoever consumes it can tell "this is
            // still true" from "this just started" and decline to page again.
            await PublishRaisedAsync(existing, isNew: false, cancellationToken);

            return existing;
        }

        var alert = Alert.Raise(Guid.CreateVersion7(), now, source, sourceId, severity, ruleKey, message, customerId);

        await _alerts.AddAsync(alert, cancellationToken);
        await _alerts.SaveChangesAsync(cancellationToken);

        await _audit.RecordAsync(
            "alert.raised",
            "Alert",
            alert.Id.ToString(),
            customerId,
            cancellationToken,
            newValue: new { ruleKey, Severity = severity.ToString(), Source = source.ToString(), sourceId });

        // Only genuinely new alerts are counted. Counting re-observations would
        // make a single six-hour outage look like a rising alert rate.
        _telemetry.AlertRaised(ruleKey, severity.ToString());

        await PublishRaisedAsync(alert, isNew: true, cancellationToken);

        return alert;
    }

    /// <summary>
    /// Announces an alert to whoever acts on alerts.
    ///
    /// Failures are swallowed, and that is the right trade: the alert is already
    /// recorded and visible on the dashboard. Letting a notification fault roll
    /// back the fleet sweep would mean one broken webhook stops KNIGHT noticing
    /// that servers are down.
    /// </summary>
    private async Task PublishRaisedAsync(Alert alert, bool isNew, CancellationToken cancellationToken)
    {
        try
        {
            await _alertEvents.PublishAsync(
                new AlertRaised(
                    alert.Id,
                    alert.RuleKey,
                    alert.Severity.ToString(),
                    alert.Message,
                    alert.SourceId,
                    alert.Source.ToString(),
                    alert.CustomerId,
                    isNew,
                    alert.LastObservedAt),
                cancellationToken);
        }
        catch (Exception exception)
        {
            _logger.LogError(
                exception,
                "Failed to publish alert {AlertId} ({RuleKey}); the alert itself is recorded.",
                alert.Id,
                alert.RuleKey);
        }
    }

    private async Task PublishResolvedAsync(Alert alert, CancellationToken cancellationToken)
    {
        try
        {
            await _alertEvents.PublishAsync(
                new AlertResolved(
                    alert.Id,
                    alert.RuleKey,
                    alert.SourceId,
                    alert.CustomerId,
                    alert.Message,
                    alert.ResolvedAt ?? _clock.UtcNow),
                cancellationToken);
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Failed to publish the resolution of alert {AlertId}.", alert.Id);
        }
    }

    /// <summary>
    /// A duration in words. "3 minutes" reads better than a timestamp in an alert
    /// somebody is scanning at speed, and the exact time is on the row anyway.
    /// </summary>
    private static string Describe(TimeSpan silence) => silence switch
    {
        { TotalDays: >= 1 } => $"{(int)silence.TotalDays} day(s)",
        { TotalHours: >= 1 } => $"{(int)silence.TotalHours} hour(s)",
        { TotalMinutes: >= 1 } => $"{(int)silence.TotalMinutes} minute(s)",
        _ => $"{(int)silence.TotalSeconds} second(s)",
    };

    private async Task<Alert> RequireAlertAsync(Guid id, CancellationToken cancellationToken) =>
        await _alerts.GetByIdAsync(id, cancellationToken)
        ?? throw new NotFoundException($"Alert '{id}' was not found.");
}
