using Knight.Domain.Common;
using Knight.Domain.Exceptions;

namespace Observability.Domain;

/// <summary>
/// One problem, however many times it has happened
/// ([`adr/0013`](../../../docs/adr/0013-error-grouping-strategy.md)).
///
/// A hundred identical errors are one thing to fix, and the list an operator
/// reads must say so. Everything here is therefore a counter or a timestamp
/// rather than a row per occurrence: the group is what grows in importance, not
/// in size.
///
/// Two decisions are worth stating plainly, because both are easy to get wrong:
///
/// * **The store version is not part of the identity.** It is recorded as "first
///   seen in" and "last seen in", so a problem survives a deployment instead of
///   being reborn as a new group every release.
/// * **Resolving is a claim about the world, and the world gets a vote.** A
///   resolved group that recurs is reopened and marked a regression rather than
///   silently counting up while displaying "Resolved" — the one state that would
///   actively mislead the person who fixed it.
/// </summary>
public sealed class ErrorGroup : AuditableEntity, ICustomerOwned
{
    public const int MaxTitleLength = 500;

    public Guid CustomerId { get; private set; }

    public Guid StoreId { get; private set; }

    /// <summary>The sha256 of the normalised signals. Unique per store together with the algorithm version.</summary>
    public string Fingerprint { get; private set; }

    /// <summary>
    /// Which version of the algorithm produced <see cref="Fingerprint"/>. Stored
    /// so the algorithm can change without corrupting history: groups written
    /// under version 1 keep their identity, and version 2 simply starts new ones.
    /// </summary>
    public int FingerprintVersion { get; private set; }

    public string Environment { get; private set; }

    public string ExceptionType { get; private set; }

    public string Title { get; private set; }

    /// <summary>The route template the failure happened on, or null when it was not a request.</summary>
    public string? Endpoint { get; private set; }

    public ErrorGroupStatus Status { get; private set; }

    public long OccurrenceCount { get; private set; }

    /// <summary>
    /// How many events are kept in full for this group. Samples are what makes a
    /// group actionable — a stack trace you can read — and also the only part of
    /// the record whose size grows, so it is bounded.
    /// </summary>
    public int SampleCount { get; private set; }

    public DateTimeOffset FirstSeenAt { get; private set; }

    public DateTimeOffset LastSeenAt { get; private set; }

    public string? FirstSeenVersion { get; private set; }

    public string? LastSeenVersion { get; private set; }

    /// <summary>Set when a resolved group recurred. The dashboard shows it as a regression.</summary>
    public DateTimeOffset? RegressedAt { get; private set; }

    public DateTimeOffset? ResolvedAt { get; private set; }

    public Guid? ResolvedBy { get; private set; }

    public DateTimeOffset? AcknowledgedAt { get; private set; }

    public Guid? AcknowledgedBy { get; private set; }

    /// <summary>
    /// The version this group was resolved at, kept so a recurrence in an older
    /// build can be told from a genuine regression in a newer one.
    /// </summary>
    public string? ResolvedInVersion { get; private set; }

    /// <summary>
    /// The incident this group was attached to, if any. Many groups may point at
    /// one incident: an outage usually breaks several things at once.
    /// </summary>
    public Guid? IncidentId { get; private set; }

    private ErrorGroup()
    {
        Fingerprint = string.Empty;
        Environment = string.Empty;
        ExceptionType = string.Empty;
        Title = string.Empty;
    }

    private ErrorGroup(
        Guid id,
        DateTimeOffset createdAt,
        Guid customerId,
        Guid storeId,
        string fingerprint,
        int fingerprintVersion,
        string environment,
        string exceptionType,
        string title,
        string? endpoint,
        string? version)
        : base(id, createdAt)
    {
        CustomerId = customerId;
        StoreId = storeId;
        Fingerprint = fingerprint;
        FingerprintVersion = fingerprintVersion;
        Environment = environment;
        ExceptionType = exceptionType;
        Title = title;
        Endpoint = endpoint;
        Status = ErrorGroupStatus.New;
        OccurrenceCount = 0;
        FirstSeenAt = createdAt;
        LastSeenAt = createdAt;
        FirstSeenVersion = version;
        LastSeenVersion = version;
    }

    public static ErrorGroup Open(
        Guid id,
        DateTimeOffset now,
        Guid customerId,
        Guid storeId,
        ErrorFingerprintResult fingerprint,
        string environment,
        string exceptionType,
        string? storeVersion)
    {
        if (storeId == Guid.Empty)
        {
            throw DomainException.Validation("An error group must belong to a store.");
        }

        if (string.IsNullOrWhiteSpace(fingerprint.Fingerprint))
        {
            throw DomainException.Validation("An error group must carry a fingerprint.");
        }

        return new ErrorGroup(
            id,
            now,
            customerId,
            storeId,
            fingerprint.Fingerprint,
            fingerprint.FingerprintVersion,
            environment.Trim(),
            exceptionType.Trim(),
            fingerprint.Title,
            string.IsNullOrEmpty(fingerprint.EndpointTemplate) ? null : fingerprint.EndpointTemplate,
            storeVersion);
    }

    /// <summary>
    /// Records that the problem happened again, and answers whether that was a
    /// regression.
    ///
    /// The caller needs the answer — a regression is worth alerting on and an
    /// ordinary recurrence is not — and only the aggregate knows, because only it
    /// knows what the status was a moment ago.
    /// </summary>
    public bool Record(DateTimeOffset occurredAt, DateTimeOffset now, string? storeVersion, bool sampled)
    {
        OccurrenceCount++;

        if (occurredAt > LastSeenAt)
        {
            LastSeenAt = occurredAt;
            LastSeenVersion = storeVersion ?? LastSeenVersion;
        }

        // Late-arriving events from a store that was offline must not make the
        // group look younger than it is.
        if (occurredAt < FirstSeenAt)
        {
            FirstSeenAt = occurredAt;
        }

        if (sampled)
        {
            SampleCount++;
        }

        var regressed = false;

        if (Status is ErrorGroupStatus.Resolved)
        {
            Status = ErrorGroupStatus.New;
            RegressedAt = now;
            ResolvedAt = null;
            ResolvedBy = null;
            regressed = true;
        }

        MarkUpdated(now);

        return regressed;
    }

    /// <summary>Somebody has seen it and taken it. Does not stop it counting.</summary>
    public void Acknowledge(Guid userId, DateTimeOffset now)
    {
        if (Status is ErrorGroupStatus.Resolved)
        {
            throw DomainException.Conflict("A resolved error group does not need acknowledging.");
        }

        Status = ErrorGroupStatus.Acknowledged;
        AcknowledgedAt = now;
        AcknowledgedBy = userId;
        MarkUpdated(now);
    }

    /// <summary>
    /// Somebody believes it is fixed. If they are wrong, <see cref="Record"/>
    /// will say so.
    /// </summary>
    public void Resolve(Guid userId, DateTimeOffset now, string? inVersion)
    {
        Status = ErrorGroupStatus.Resolved;
        ResolvedAt = now;
        ResolvedBy = userId;
        ResolvedInVersion = inVersion ?? LastSeenVersion;
        RegressedAt = null;
        MarkUpdated(now);
    }

    /// <summary>
    /// Known, understood and not worth acting on — a third-party crawler probing
    /// for PHP files, say. Ignored groups keep counting but never alert and never
    /// reopen, because being told again about something you already dismissed is
    /// how an alert channel becomes unread.
    /// </summary>
    public void Ignore(Guid userId, DateTimeOffset now)
    {
        Status = ErrorGroupStatus.Ignored;
        AcknowledgedAt = now;
        AcknowledgedBy = userId;
        MarkUpdated(now);
    }

    /// <summary>Undoes an ignore or a resolve without waiting for the problem to prove the point.</summary>
    public void Reopen(DateTimeOffset now)
    {
        Status = ErrorGroupStatus.New;
        ResolvedAt = null;
        ResolvedBy = null;
        MarkUpdated(now);
    }

    public void AttachToIncident(Guid incidentId, DateTimeOffset now)
    {
        if (incidentId == Guid.Empty)
        {
            throw DomainException.Validation("An error group must be attached to a real incident.");
        }

        IncidentId = incidentId;
        MarkUpdated(now);
    }

    public void DetachFromIncident(DateTimeOffset now)
    {
        IncidentId = null;
        MarkUpdated(now);
    }

    /// <summary>
    /// Whether this group should be allowed to raise alerts. Ignored groups never
    /// do, and neither do groups somebody is already holding.
    /// </summary>
    public bool IsAlertable => Status is ErrorGroupStatus.New;

    public bool IsRegression => RegressedAt is not null;
}

public enum ErrorGroupStatus
{
    New = 0,
    Acknowledged = 1,
    Resolved = 2,
    Ignored = 3,
}
