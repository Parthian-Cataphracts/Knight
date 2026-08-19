using Knight.Domain.Common;
using Knight.Domain.Exceptions;

namespace Servers.Domain;

/// <summary>
/// Something is wrong and somebody should know (docs/observability.md §8).
///
/// The design decision that matters most here is that an alert is **deduplicated
/// by rule and source**. A server that has been offline for six hours is one
/// alert that has been open for six hours, not seven hundred rows. Without that,
/// the alert list becomes unreadable exactly when it matters, and the honest
/// signal — how long has this been broken — is lost in the noise.
///
/// An alert is resolved by the condition clearing, not by a person acknowledging
/// it. Acknowledgement records that somebody is looking; only the world getting
/// better closes it.
/// </summary>
public sealed class Alert : AuditableEntity
{
    public AlertSource Source { get; private set; }

    public Guid SourceId { get; private set; }

    /// <summary>
    /// Which customer this concerns, when it concerns one. Null for platform
    /// infrastructure: a shared server going down is not one customer's alert to
    /// read, even though it affects them.
    /// </summary>
    public Guid? CustomerId { get; private set; }

    public AlertSeverity Severity { get; private set; }

    /// <summary>The rule that raised it, such as <c>server.offline</c>. What deduplication and routing key on.</summary>
    public string RuleKey { get; private set; }

    public string Message { get; private set; }

    public DateTimeOffset RaisedAt { get; private set; }

    public DateTimeOffset? ResolvedAt { get; private set; }

    public DateTimeOffset? AcknowledgedAt { get; private set; }

    public Guid? AcknowledgedBy { get; private set; }

    /// <summary>
    /// How many times the condition has been observed since it was raised. A
    /// count is the cheapest possible way to tell a blip from a persistent fault
    /// without keeping a row per observation.
    /// </summary>
    public int OccurrenceCount { get; private set; }

    public DateTimeOffset LastObservedAt { get; private set; }

    private Alert()
    {
        RuleKey = string.Empty;
        Message = string.Empty;
    }

    private Alert(
        Guid id,
        DateTimeOffset createdAt,
        AlertSource source,
        Guid sourceId,
        Guid? customerId,
        AlertSeverity severity,
        string ruleKey,
        string message)
        : base(id, createdAt)
    {
        Source = source;
        SourceId = sourceId;
        CustomerId = customerId;
        Severity = severity;
        RuleKey = ruleKey;
        Message = message;
        RaisedAt = createdAt;
        LastObservedAt = createdAt;
        OccurrenceCount = 1;
    }

    public static Alert Raise(
        Guid id,
        DateTimeOffset now,
        AlertSource source,
        Guid sourceId,
        AlertSeverity severity,
        string ruleKey,
        string message,
        Guid? customerId = null)
    {
        if (sourceId == Guid.Empty)
        {
            throw DomainException.Validation("An alert must name what it is about.");
        }

        if (string.IsNullOrWhiteSpace(ruleKey))
        {
            throw DomainException.Validation("An alert must name the rule that raised it.");
        }

        if (string.IsNullOrWhiteSpace(message))
        {
            throw DomainException.Validation("An alert must say what is wrong.");
        }

        return new Alert(
            id,
            now,
            source,
            sourceId,
            customerId,
            severity,
            ruleKey.Trim(),
            message.Trim().Length <= 1000 ? message.Trim() : message.Trim()[..1000]);
    }

    /// <summary>
    /// Records that the condition is still true.
    ///
    /// The message is refreshed because the detail usually moves — "offline for
    /// 5 minutes" becomes "offline for 3 hours" — and a stale message is worse
    /// than none: it tells an operator the wrong thing with total confidence.
    /// </summary>
    public void Observe(string message, DateTimeOffset now)
    {
        if (ResolvedAt is not null)
        {
            throw DomainException.Conflict("A resolved alert cannot be observed again; raise a new one.");
        }

        OccurrenceCount++;
        LastObservedAt = now;

        if (!string.IsNullOrWhiteSpace(message))
        {
            Message = message.Trim().Length <= 1000 ? message.Trim() : message.Trim()[..1000];
        }

        MarkUpdated(now);
    }

    /// <summary>Somebody is looking. Does not close the alert.</summary>
    public void Acknowledge(Guid userId, DateTimeOffset now)
    {
        if (ResolvedAt is not null)
        {
            throw DomainException.Conflict("A resolved alert does not need acknowledging.");
        }

        AcknowledgedAt = now;
        AcknowledgedBy = userId;
        MarkUpdated(now);
    }

    /// <summary>The condition cleared.</summary>
    public void Resolve(DateTimeOffset now)
    {
        if (ResolvedAt is not null)
        {
            return;
        }

        ResolvedAt = now;
        MarkUpdated(now);
    }

    public bool IsOpen => ResolvedAt is null;

    /// <summary>How long the condition has been true, for the dashboard to show at a glance.</summary>
    public TimeSpan Duration(DateTimeOffset now) => (ResolvedAt ?? now) - RaisedAt;
}

public enum AlertSource
{
    Server = 0,
    Store = 1,
    Agent = 2,
    FeatureInstallation = 3,
}

public enum AlertSeverity
{
    Info = 0,
    Warning = 1,
    Critical = 2,
}

/// <summary>
/// The rule keys of docs/observability.md §8, as constants.
///
/// Constants rather than free strings because these are what deduplication,
/// routing and the dashboard's filters all key on. A typo in a rule key does not
/// fail — it quietly creates a second, parallel alert stream that nobody is
/// watching.
/// </summary>
public static class AlertRules
{
    public const string ServerOffline = "server.offline";
    public const string ServerDegraded = "server.degraded";
    public const string ServerDiskCritical = "server.disk.critical";
    public const string AgentOffline = "agent.offline";
    public const string FeatureInstallFailed = "feature.install.failed";
    public const string JobStuck = "job.stuck";
    public const string StoreUnreachable = "store.unreachable";
}
