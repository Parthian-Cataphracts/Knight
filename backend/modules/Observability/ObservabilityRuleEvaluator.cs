using Knight.Application.Abstractions.ControlPlane;
using Knight.Application.Abstractions.Time;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Observability.Domain;

namespace Observability;

/// <summary>
/// One pass of every rule that cannot be evaluated at the moment something
/// happens.
///
/// The rules here share one property: each is about a *difference that has
/// persisted*, not about an event. Nothing reports "my entitlement was never
/// installed" and nothing reports "my rate has quintupled" — those are only
/// visible to something that looks at the whole picture on a timer, which is
/// exactly what this is (docs/observability.md §8).
///
/// Every rule is written so that running it twice changes nothing the second
/// time. Alert deduplication does the work: a rule raises the same rule key and
/// subject every pass, and the alerting layer turns the second and subsequent
/// raises into observations of one open alert.
/// </summary>
public interface IObservabilityRuleEvaluator
{
    Task<RuleEvaluationResult> EvaluateAsync(CancellationToken cancellationToken);
}

/// <summary>What one pass found. Returned rather than logged so a test can assert on it.</summary>
public sealed record RuleEvaluationResult(
    int Spikes,
    int FailedInstalls,
    int EntitledNotInstalled,
    int Drifted,
    int StuckJobs,
    int OverdueBackups,
    int DeadLetters,
    int UnreachableServices)
{
    public int Total =>
        Spikes + FailedInstalls + EntitledNotInstalled + Drifted + StuckJobs + OverdueBackups
        + DeadLetters + UnreachableServices;
}

internal sealed class ObservabilityRuleEvaluator : IObservabilityRuleEvaluator
{
    private readonly IErrorGroupRepository _groups;
    private readonly IErrorGroupEventReader _events;
    private readonly IDeliveryHealthReader _delivery;
    private readonly IBackupHealthReader _backups;
    private readonly IAlertRaiser _alerts;
    private readonly IDateTimeProvider _clock;
    private readonly ILogger<ObservabilityRuleEvaluator> _logger;
    private readonly ObservabilityOptions _options;

    public ObservabilityRuleEvaluator(
        IErrorGroupRepository groups,
        IErrorGroupEventReader events,
        IDeliveryHealthReader delivery,
        IBackupHealthReader backups,
        IAlertRaiser alerts,
        IDateTimeProvider clock,
        ILogger<ObservabilityRuleEvaluator> logger,
        IOptions<ObservabilityOptions> options)
    {
        _groups = groups;
        _events = events;
        _delivery = delivery;
        _backups = backups;
        _alerts = alerts;
        _clock = clock;
        _logger = logger;
        _options = options.Value;
    }

    public async Task<RuleEvaluationResult> EvaluateAsync(CancellationToken cancellationToken)
    {
        var now = _clock.UtcNow;

        var spikes = await EvaluateSpikesAsync(now, cancellationToken);
        var failed = await EvaluateFailedInstallsAsync(now, cancellationToken);
        var missing = await EvaluateEntitledNotInstalledAsync(now, cancellationToken);
        var drifted = await EvaluateDriftAsync(cancellationToken);
        var stuck = await EvaluateStuckJobsAsync(now, cancellationToken);
        var backups = await EvaluateOverdueBackupsAsync(now, cancellationToken);
        var (deadLetters, unreachable) = await EvaluateReportedFailuresAsync(now, cancellationToken);

        return new RuleEvaluationResult(
            spikes, failed, missing, drifted, stuck, backups, deadLetters, unreachable);
    }

    /// <summary>
    /// A group whose recent rate is far above its own established rate.
    ///
    /// Comparing a group against itself rather than against a global threshold is
    /// the whole point: a checkout endpoint that throws twice an hour every hour
    /// is not news, and the same endpoint throwing two hundred times in fifteen
    /// minutes is — even though a fixed threshold would either miss the second or
    /// fire constantly on the first.
    /// </summary>
    private async Task<int> EvaluateSpikesAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        var since = now - _options.SpikeWindow;
        var raised = 0;

        foreach (var group in await _groups.ListSeenSinceAsync(since, cancellationToken))
        {
            // Acknowledged, resolved and ignored groups are excluded: somebody
            // has already made a decision about this one, and a spike alert is
            // that decision being overruled by a counter.
            if (!group.IsAlertable)
            {
                continue;
            }

            var recent = await _events.CountSinceAsync(group.Id, since, cancellationToken);

            if (recent < _options.SpikeMinimumCount)
            {
                continue;
            }

            // The baseline is the group's whole history expressed at the window's
            // scale. A group that has only ever existed inside this window has no
            // baseline to exceed, so its first window is never a spike — it is
            // simply the group appearing, which the errors screen already shows.
            var lifetime = group.LastSeenAt - group.FirstSeenAt;

            if (lifetime <= _options.SpikeWindow)
            {
                continue;
            }

            var expected = group.OccurrenceCount * (_options.SpikeWindow.TotalSeconds / lifetime.TotalSeconds);

            if (expected <= 0 || recent < expected * _options.SpikeMultiplier)
            {
                continue;
            }

            var (_, isNew) = await _alerts.RaiseAsync(
                ObservabilityRules.ErrorSpike,
                nameof(NotificationSeverity.Critical),
                "Store",
                group.Id,
                group.CustomerId,
                $"{group.Title} occurred {recent} times in the last {_options.SpikeWindow.TotalMinutes:0} minutes " +
                $"— about {recent / Math.Max(expected, 1):0}x its usual rate.",
                cancellationToken);

            if (isNew)
            {
                raised++;
            }
        }

        return raised;
    }

    private async Task<int> EvaluateFailedInstallsAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        var failures = await _delivery.ListFailedJobsAsync(now - _options.FailureLookback, cancellationToken);

        return await RaiseEachAsync(
            failures,
            ObservabilityRules.FeatureInstallFailed,
            NotificationSeverity.Critical,
            discrepancy => $"Installing {discrepancy.FeatureSlug} on {discrepancy.StoreName} failed: {discrepancy.Detail}",
            cancellationToken);
    }

    /// <summary>
    /// The customer is paying for something that is not running. Of every rule
    /// here this is the one that costs money to leave broken, and the one nobody
    /// would otherwise notice: the dashboard shows the entitlement, the store
    /// shows nothing, and neither screen is wrong.
    /// </summary>
    private async Task<int> EvaluateEntitledNotInstalledAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        var missing = await _delivery.ListEntitledNotInstalledAsync(now - _options.InstallationGrace, cancellationToken);

        return await RaiseEachAsync(
            missing,
            ObservabilityRules.FeatureEntitledNotInstalled,
            NotificationSeverity.Warning,
            discrepancy =>
                $"{discrepancy.StoreName} is entitled to {discrepancy.FeatureSlug} but it is not installed. {discrepancy.Detail}",
            cancellationToken);
    }

    /// <summary>
    /// KNIGHT believes one thing is deployed and the store reports another.
    /// Either somebody changed a store by hand or an install half-succeeded, and
    /// both mean the control plane's picture of the world is wrong — which makes
    /// every later decision it takes about that store suspect.
    /// </summary>
    private async Task<int> EvaluateDriftAsync(CancellationToken cancellationToken)
    {
        var drifted = await _delivery.ListDriftedAsync(cancellationToken);

        return await RaiseEachAsync(
            drifted,
            ObservabilityRules.FeatureDrift,
            NotificationSeverity.Warning,
            discrepancy => $"{discrepancy.StoreName} reports {discrepancy.FeatureSlug} {discrepancy.Detail}",
            cancellationToken);
    }

    /// <summary>
    /// What stores have told KNIGHT about delivering to a Feature's service.
    ///
    /// The two failures this architecture added and nothing could see: an event
    /// that used every attempt and was dead-lettered, and a service that did not
    /// answer a request a shopper was waiting on. Both are handled correctly and
    /// locally by the store — the queue keeps the dead letter, the proxy returns
    /// a 502 — and both are invisible to anybody not reading that store's log.
    ///
    /// Grouped by store, Feature and kind before anything is raised: a service
    /// that has been down for an hour produced hundreds of reports, and one
    /// alert per report would be a pager nobody would keep on
    /// (<c>docs/runbooks.md</c>).
    /// </summary>
    private async Task<(int DeadLetters, int Unreachable)> EvaluateReportedFailuresAsync(
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var reported = await _delivery.ListStoreReportedFailuresAsync(
            now - _options.ReportedFailureWindow,
            [StoreFailureKinds.DeadLettered, StoreFailureKinds.Unreachable, StoreFailureKinds.Unconfigured],
            cancellationToken);

        var deadLetters = 0;
        var unreachable = 0;

        foreach (var failure in reported)
        {
            var isDeadLetter = string.Equals(failure.Kind, StoreFailureKinds.DeadLettered, StringComparison.Ordinal);

            var rule = isDeadLetter ? ObservabilityRules.DeliveryDeadLettered : ObservabilityRules.ServiceUnreachable;

            // A dead letter is critical and an unreachable service is a warning,
            // and the difference is whether anything was lost. A 502 is a
            // shopper retrying a minute later; a dead letter is an event that
            // will never be delivered to a Feature somebody is paying for.
            var severity = isDeadLetter ? NotificationSeverity.Critical : NotificationSeverity.Warning;

            var times = failure.Count == 1 ? "once" : $"{failure.Count} times";

            try
            {
                var (_, isNew) = await _alerts.RaiseAsync(
                    rule,
                    severity.ToString(),
                    "Store",
                    failure.StoreId,
                    failure.CustomerId,
                    $"'{failure.StoreName}' reported {rule} for {failure.FeatureSlug} {times}. {failure.Detail}",
                    cancellationToken);

                if (isNew && isDeadLetter)
                {
                    deadLetters++;
                }
                else if (isNew)
                {
                    unreachable++;
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                _logger.LogWarning(
                    exception,
                    "Failed to raise {RuleKey} for store {StoreId}; the rest of the pass continues.",
                    rule,
                    failure.StoreId);
            }
        }

        return (deadLetters, unreachable);
    }

    private async Task<int> EvaluateStuckJobsAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        var stuck = await _delivery.ListStuckJobsAsync(now - _options.StuckJobThreshold, cancellationToken);

        return await RaiseEachAsync(
            stuck,
            ObservabilityRules.JobStuck,
            NotificationSeverity.Warning,
            discrepancy =>
                $"A {discrepancy.FeatureSlug} job on {discrepancy.StoreName} was claimed and never reported again. {discrepancy.Detail}",
            cancellationToken);
    }

    /// <summary>
    /// Stores whose backups have stopped happening.
    ///
    /// Alerting on an absence, which is why it needs a sweep at all: a failed
    /// backup reports itself and raises <c>backup.failed</c> the moment it is
    /// reported. A backup job that was switched off, or a store that quietly
    /// stopped reporting, says nothing — and looks identical to a healthy store
    /// on every other screen KNIGHT has.
    /// </summary>
    private async Task<int> EvaluateOverdueBackupsAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        var stale = await _backups.ListStoresWithoutRecentBackupAsync(now - _options.BackupInterval, cancellationToken);
        var raised = 0;

        foreach (var store in stale)
        {
            var since = store.LastSuccessfulBackupAt is { } last
                ? $"The last successful backup was {(now - last).TotalHours:0} hours ago."
                : "No successful backup has ever been reported for it.";

            try
            {
                var (_, isNew) = await _alerts.RaiseAsync(
                    ObservabilityRules.BackupOverdue,
                    nameof(NotificationSeverity.Critical),
                    "Store",
                    store.StoreId,
                    store.CustomerId,
                    $"'{store.StoreName}' has no recent backup. {since}",
                    cancellationToken);

                if (isNew)
                {
                    raised++;
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                _logger.LogWarning(
                    exception,
                    "Failed to raise {RuleKey} for store {StoreId}; the rest of the pass continues.",
                    ObservabilityRules.BackupOverdue,
                    store.StoreId);
            }
        }

        return raised;
    }

    /// <summary>
    /// Raises one alert per discrepancy and counts the genuinely new ones.
    ///
    /// One failure does not stop the pass: a rule that gave up at the first
    /// problem would be least useful exactly when most things are wrong.
    /// </summary>
    private async Task<int> RaiseEachAsync(
        IReadOnlyCollection<DeliveryDiscrepancy> discrepancies,
        string ruleKey,
        NotificationSeverity severity,
        Func<DeliveryDiscrepancy, string> message,
        CancellationToken cancellationToken)
    {
        var raised = 0;

        foreach (var discrepancy in discrepancies)
        {
            try
            {
                var (_, isNew) = await _alerts.RaiseAsync(
                    ruleKey,
                    severity.ToString(),
                    "FeatureInstallation",
                    discrepancy.SubjectId,
                    discrepancy.CustomerId,
                    message(discrepancy),
                    cancellationToken);

                if (isNew)
                {
                    raised++;
                }
            }
            catch (Exception exception)
            {
                _logger.LogError(
                    exception,
                    "Failed to raise {RuleKey} for subject {SubjectId}; the rest of the pass continues.",
                    ruleKey,
                    discrepancy.SubjectId);
            }
        }

        return raised;
    }
}
