using Knight.Domain.Common;
using Knight.Domain.Exceptions;

namespace Observability.Domain;

/// <summary>
/// Something is wrong badly enough that the response needs a record of its own
/// (docs/observability.md §8).
///
/// An incident is not an alert and not an error group. An alert says a condition
/// is true; an error group says a problem exists. An incident says *people are
/// dealing with this*, and it carries the one thing neither of the others can:
/// a timeline of what was noticed, decided and done, in order, with who did it.
/// That timeline is the entire point — it is what makes a post-mortem possible
/// and what stops the same outage being diagnosed from scratch twice.
///
/// Incidents are opened by a rule or by a person, and closed only by a person.
/// A rule that could close an incident would close it the moment the symptom
/// stopped, which is reliably earlier than the moment the problem ended.
/// </summary>
public sealed class Incident : AuditableEntity, ICustomerScoped
{
    public const int MaxTitleLength = 300;
    public const int MaxSummaryLength = 4000;

    /// <summary>
    /// A short human reference — <c>INC-2026-0042</c> — for talking about it in
    /// a chat window where a guid would be retyped wrong.
    /// </summary>
    public string Reference { get; private set; }

    public string Title { get; private set; }

    public string? Summary { get; private set; }

    public IncidentSeverity Severity { get; private set; }

    public IncidentStatus Status { get; private set; }

    /// <summary>Null when the incident is platform-wide rather than one customer's.</summary>
    public Guid? CustomerId { get; private set; }

    public Guid? StoreId { get; private set; }

    public Guid? ServerId { get; private set; }

    /// <summary>The rule that opened it, or null when a person did.</summary>
    public string? RuleKey { get; private set; }

    public DateTimeOffset OpenedAt { get; private set; }

    public Guid? OpenedBy { get; private set; }

    public DateTimeOffset? AcknowledgedAt { get; private set; }

    public DateTimeOffset? MitigatedAt { get; private set; }

    public DateTimeOffset? ResolvedAt { get; private set; }

    public Guid? ResolvedBy { get; private set; }

    /// <summary>
    /// Why it happened, written afterwards. Deliberately free text and
    /// deliberately not required: an incomplete post-mortem is worth more than a
    /// mandatory field somebody filled with a full stop.
    /// </summary>
    public string? RootCause { get; private set; }

    private readonly List<IncidentEvent> _timeline = [];

    public IReadOnlyCollection<IncidentEvent> Timeline => _timeline.AsReadOnly();

    private Incident()
    {
        Reference = string.Empty;
        Title = string.Empty;
    }

    private Incident(
        Guid id,
        DateTimeOffset createdAt,
        string reference,
        string title,
        IncidentSeverity severity,
        Guid? customerId,
        Guid? storeId,
        Guid? serverId,
        string? ruleKey,
        Guid? openedBy)
        : base(id, createdAt)
    {
        Reference = reference;
        Title = title;
        Severity = severity;
        Status = IncidentStatus.Open;
        CustomerId = customerId;
        StoreId = storeId;
        ServerId = serverId;
        RuleKey = ruleKey;
        OpenedAt = createdAt;
        OpenedBy = openedBy;
    }

    public static Incident Open(
        Guid id,
        DateTimeOffset now,
        string reference,
        string title,
        IncidentSeverity severity,
        Guid? customerId = null,
        Guid? storeId = null,
        Guid? serverId = null,
        string? ruleKey = null,
        Guid? openedBy = null,
        string? summary = null)
    {
        if (string.IsNullOrWhiteSpace(reference))
        {
            throw DomainException.Validation("An incident must have a reference.");
        }

        if (string.IsNullOrWhiteSpace(title))
        {
            throw DomainException.Validation("An incident must say what it is about.");
        }

        var incident = new Incident(
            id,
            now,
            reference.Trim(),
            Clip(title, MaxTitleLength),
            severity,
            customerId,
            storeId,
            serverId,
            string.IsNullOrWhiteSpace(ruleKey) ? null : ruleKey.Trim(),
            openedBy);

        if (!string.IsNullOrWhiteSpace(summary))
        {
            incident.Summary = Clip(summary, MaxSummaryLength);
        }

        // Opening is itself the first timeline entry. Recording it here rather
        // than leaving it to the caller means no incident can exist without one.
        incident.Append(
            IncidentEventType.Opened,
            now,
            openedBy,
            ruleKey is null ? "Opened manually." : $"Opened automatically by rule {ruleKey}.");

        return incident;
    }

    /// <summary>
    /// Somebody is on it. Separate from a status change because acknowledging is
    /// about response time, and response time is the number that gets measured.
    /// </summary>
    public void Acknowledge(Guid userId, DateTimeOffset now, string? note = null)
    {
        RequireOpenForWork();

        AcknowledgedAt ??= now;
        Status = IncidentStatus.Investigating;

        Append(IncidentEventType.StatusChanged, now, userId, note ?? "Investigation started.");
        MarkUpdated(now);
    }

    /// <summary>
    /// The bleeding has stopped, the cause has not necessarily been found. This
    /// distinction is why <see cref="IncidentStatus.Mitigated"/> exists at all:
    /// collapsing it into "resolved" loses the window where a workaround is
    /// holding and can still fail.
    /// </summary>
    public void Mitigate(Guid userId, DateTimeOffset now, string note)
    {
        RequireOpenForWork();

        if (string.IsNullOrWhiteSpace(note))
        {
            throw DomainException.Validation("Say what mitigated it; an unexplained mitigation cannot be reviewed.");
        }

        MitigatedAt ??= now;
        Status = IncidentStatus.Mitigated;

        Append(IncidentEventType.Mitigated, now, userId, note);
        MarkUpdated(now);
    }

    public void Resolve(Guid userId, DateTimeOffset now, string? rootCause = null)
    {
        if (Status is IncidentStatus.Resolved)
        {
            throw DomainException.Conflict("This incident is already resolved.");
        }

        Status = IncidentStatus.Resolved;
        ResolvedAt = now;
        ResolvedBy = userId;

        if (!string.IsNullOrWhiteSpace(rootCause))
        {
            RootCause = Clip(rootCause, MaxSummaryLength);
        }

        Append(IncidentEventType.Resolved, now, userId, rootCause ?? "Resolved.");
        MarkUpdated(now);
    }

    /// <summary>
    /// It was not over. Reopening keeps the original timeline rather than
    /// starting a second incident, because the two halves are one story.
    /// </summary>
    public void Reopen(Guid userId, DateTimeOffset now, string reason)
    {
        if (Status is not IncidentStatus.Resolved)
        {
            throw DomainException.Conflict("Only a resolved incident can be reopened.");
        }

        Status = IncidentStatus.Investigating;
        ResolvedAt = null;
        ResolvedBy = null;

        Append(IncidentEventType.StatusChanged, now, userId, $"Reopened: {reason}");
        MarkUpdated(now);
    }

    /// <summary>A note from a responder, or from the system when a rule fires again.</summary>
    public void AddNote(Guid? userId, DateTimeOffset now, string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            throw DomainException.Validation("A note must say something.");
        }

        Append(IncidentEventType.Note, now, userId, message);
        MarkUpdated(now);
    }

    /// <summary>
    /// Raises the severity when a second, worse signal arrives. It never lowers
    /// it automatically: an incident that quietly de-escalated itself while the
    /// impact continued is the failure mode this guards against.
    /// </summary>
    public void Escalate(IncidentSeverity severity, DateTimeOffset now, string reason)
    {
        if (severity <= Severity)
        {
            return;
        }

        Severity = severity;
        Append(IncidentEventType.StatusChanged, now, null, $"Escalated to {severity}: {reason}");
        MarkUpdated(now);
    }

    private void RequireOpenForWork()
    {
        if (Status is IncidentStatus.Resolved)
        {
            throw DomainException.Conflict("This incident is resolved; reopen it first.");
        }
    }

    private void Append(IncidentEventType type, DateTimeOffset now, Guid? actorId, string message)
    {
        _timeline.Add(IncidentEvent.Record(Guid.NewGuid(), Id, now, type, actorId, message));
    }

    public bool IsOpen => Status is not IncidentStatus.Resolved;

    public TimeSpan Duration(DateTimeOffset now) => (ResolvedAt ?? now) - OpenedAt;

    private static string Clip(string value, int max)
    {
        var trimmed = value.Trim();

        return trimmed.Length <= max ? trimmed : trimmed[..max];
    }
}

/// <summary>
/// One entry in an incident's timeline. Append-only by design: an incident
/// record you can edit after the fact is a record nobody can rely on.
/// </summary>
public sealed class IncidentEvent : Entity
{
    public const int MaxMessageLength = 2000;

    public Guid IncidentId { get; private set; }

    public DateTimeOffset OccurredAt { get; private set; }

    public IncidentEventType Type { get; private set; }

    /// <summary>Null when the system did it rather than a person.</summary>
    public Guid? ActorId { get; private set; }

    public string Message { get; private set; }

    private IncidentEvent()
    {
        Message = string.Empty;
    }

    private IncidentEvent(
        Guid id,
        Guid incidentId,
        DateTimeOffset occurredAt,
        IncidentEventType type,
        Guid? actorId,
        string message)
        : base(id)
    {
        IncidentId = incidentId;
        OccurredAt = occurredAt;
        Type = type;
        ActorId = actorId;
        Message = message;
    }

    public static IncidentEvent Record(
        Guid id,
        Guid incidentId,
        DateTimeOffset occurredAt,
        IncidentEventType type,
        Guid? actorId,
        string message)
    {
        var trimmed = (message ?? string.Empty).Trim();

        return new IncidentEvent(
            id,
            incidentId,
            occurredAt,
            type,
            actorId,
            trimmed.Length <= MaxMessageLength ? trimmed : trimmed[..MaxMessageLength]);
    }
}

/// <summary>
/// Ordered from least to most serious so <see cref="Incident.Escalate"/> can
/// compare them directly. Changing the numbers changes that comparison.
/// </summary>
public enum IncidentSeverity
{
    Info = 0,
    Warning = 1,
    Critical = 2,
}

public enum IncidentStatus
{
    Open = 0,
    Investigating = 1,
    Mitigated = 2,
    Resolved = 3,
}

public enum IncidentEventType
{
    Opened = 0,
    Note = 1,
    StatusChanged = 2,
    Mitigated = 3,
    Resolved = 4,
}
