using Knight.Domain.Common;
using Knight.Domain.Exceptions;

namespace Observability.Domain;

/// <summary>
/// Somewhere a notification can be sent (docs/observability.md §8).
///
/// A channel is configuration, not a message. It is kept separate from the
/// deliveries that go through it so that a broken webhook can be fixed once
/// rather than per notification, and so that the history of what was sent
/// survives the channel being deleted.
///
/// The endpoint is stored as given and validated on the way in. Nothing here
/// decides where a webhook may point — that judgement belongs to the outbound
/// address policy at send time, on the *resolved* address, because a hostname
/// that resolves publicly today can resolve to a link-local address tomorrow
/// (docs/security.md, SSRF).
/// </summary>
public sealed class NotificationChannel : AuditableEntity, ICustomerScoped
{
    public const int MaxNameLength = 200;
    public const int MaxEndpointLength = 1000;

    /// <summary>Null for a platform channel — the on-call webhook for the operators themselves.</summary>
    public Guid? CustomerId { get; private set; }

    public string Name { get; private set; }

    public NotificationChannelKind Kind { get; private set; }

    /// <summary>An email address, a webhook URL, or null for the in-app channel, which has no destination outside KNIGHT.</summary>
    public string? Endpoint { get; private set; }

    /// <summary>
    /// The shared secret a webhook payload is signed with, encrypted at rest.
    /// A receiver that cannot tell a real notification from a forged one is a
    /// receiver that must not act on either.
    /// </summary>
    public string? SecretCipher { get; private set; }

    /// <summary>
    /// The lowest severity that reaches this channel. The single most effective
    /// control over whether anyone still reads it a month from now.
    /// </summary>
    public NotificationSeverity MinimumSeverity { get; private set; }

    /// <summary>
    /// Rule keys this channel wants, or empty for all of them at or above the
    /// severity floor. Stored as a comma-separated list because it is a short
    /// set that is only ever read whole.
    /// </summary>
    public string? RuleFilter { get; private set; }

    public bool IsEnabled { get; private set; }

    /// <summary>
    /// Turned off by the dispatcher after repeated hard failures, with the reason
    /// kept. A channel that has been rejecting every delivery for a week is not
    /// worth retrying forever, and silently continuing to try hides the fact that
    /// nobody has been notified of anything.
    /// </summary>
    public DateTimeOffset? DisabledAt { get; private set; }

    public string? DisabledReason { get; private set; }

    public DateTimeOffset? LastDeliveredAt { get; private set; }

    public int ConsecutiveFailures { get; private set; }

    private NotificationChannel()
    {
        Name = string.Empty;
    }

    private NotificationChannel(
        Guid id,
        DateTimeOffset createdAt,
        Guid? customerId,
        string name,
        NotificationChannelKind kind,
        string? endpoint,
        string? secretCipher,
        NotificationSeverity minimumSeverity,
        string? ruleFilter)
        : base(id, createdAt)
    {
        CustomerId = customerId;
        Name = name;
        Kind = kind;
        Endpoint = endpoint;
        SecretCipher = secretCipher;
        MinimumSeverity = minimumSeverity;
        RuleFilter = ruleFilter;
        IsEnabled = true;
    }

    public static NotificationChannel Create(
        Guid id,
        DateTimeOffset now,
        Guid? customerId,
        string name,
        NotificationChannelKind kind,
        string? endpoint,
        NotificationSeverity minimumSeverity,
        IEnumerable<string>? ruleFilter = null,
        string? secretCipher = null)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw DomainException.Validation("A notification channel must have a name.");
        }

        var destination = NormaliseEndpoint(kind, endpoint);

        return new NotificationChannel(
            id,
            now,
            customerId,
            name.Trim()[..Math.Min(name.Trim().Length, MaxNameLength)],
            kind,
            destination,
            secretCipher,
            minimumSeverity,
            NormaliseFilter(ruleFilter));
    }

    private static string? NormaliseEndpoint(NotificationChannelKind kind, string? endpoint)
    {
        if (kind is NotificationChannelKind.InApp)
        {
            // An in-app channel delivers into KNIGHT's own notification list.
            // Accepting a destination for it would imply it went somewhere else.
            return null;
        }

        if (string.IsNullOrWhiteSpace(endpoint))
        {
            throw DomainException.Validation($"A {kind} channel needs a destination.");
        }

        var trimmed = endpoint.Trim();

        if (trimmed.Length > MaxEndpointLength)
        {
            throw DomainException.Validation("The destination is too long.");
        }

        switch (kind)
        {
            case NotificationChannelKind.Email:
                // Deliberately shallow. Full RFC 5322 validation rejects
                // addresses that work and accepts ones that do not; the real
                // check is whether mail is accepted at send time.
                if (!trimmed.Contains('@', StringComparison.Ordinal) || trimmed.StartsWith('@') || trimmed.EndsWith('@'))
                {
                    throw DomainException.Validation("That does not look like an email address.");
                }

                break;

            case NotificationChannelKind.Webhook:
                if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) ||
                    (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
                {
                    throw DomainException.Validation("A webhook destination must be an absolute http or https URL.");
                }

                break;
        }

        return trimmed;
    }

    private static string? NormaliseFilter(IEnumerable<string>? rules)
    {
        var keys = (rules ?? [])
            .Where(rule => !string.IsNullOrWhiteSpace(rule))
            .Select(rule => rule.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return keys.Length == 0 ? null : string.Join(',', keys);
    }

    /// <summary>
    /// Whether a notification of this severity and rule belongs here. The
    /// routing decision lives on the channel because the channel is the thing
    /// that knows what its owner asked for.
    /// </summary>
    public bool Accepts(NotificationSeverity severity, string ruleKey)
    {
        if (!IsEnabled || severity < MinimumSeverity)
        {
            return false;
        }

        if (RuleFilter is null)
        {
            return true;
        }

        return RuleFilter
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(rule => string.Equals(rule, ruleKey, StringComparison.OrdinalIgnoreCase));
    }

    public void Update(
        string name,
        NotificationSeverity minimumSeverity,
        IEnumerable<string>? ruleFilter,
        DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw DomainException.Validation("A notification channel must have a name.");
        }

        Name = name.Trim()[..Math.Min(name.Trim().Length, MaxNameLength)];
        MinimumSeverity = minimumSeverity;
        RuleFilter = NormaliseFilter(ruleFilter);
        MarkUpdated(now);
    }

    public void Enable(DateTimeOffset now)
    {
        IsEnabled = true;
        DisabledAt = null;
        DisabledReason = null;
        ConsecutiveFailures = 0;
        MarkUpdated(now);
    }

    public void Disable(string reason, DateTimeOffset now)
    {
        IsEnabled = false;
        DisabledAt = now;
        DisabledReason = reason.Trim()[..Math.Min(reason.Trim().Length, 500)];
        MarkUpdated(now);
    }

    public void RecordSuccess(DateTimeOffset now)
    {
        LastDeliveredAt = now;
        ConsecutiveFailures = 0;
        MarkUpdated(now);
    }

    /// <summary>
    /// Records a failure and answers whether the channel has now failed often
    /// enough to be switched off.
    /// </summary>
    public bool RecordFailure(int threshold, string reason, DateTimeOffset now)
    {
        ConsecutiveFailures++;
        MarkUpdated(now);

        if (ConsecutiveFailures < threshold || !IsEnabled)
        {
            return false;
        }

        Disable($"Disabled after {ConsecutiveFailures} consecutive failures: {reason}", now);

        return true;
    }
}

/// <summary>
/// One attempt to tell somebody something, and its outcome.
///
/// Deliveries are recorded rather than fired and forgotten because "was anyone
/// actually told?" is a question that gets asked after every incident, and the
/// honest answer requires a row. A pending row that never became sent is itself
/// the finding.
/// </summary>
public sealed class NotificationDelivery : AuditableEntity, ICustomerScoped
{
    public const int MaxSubjectLength = 300;
    public const int MaxBodyLength = 4000;

    public Guid ChannelId { get; private set; }

    public Guid? CustomerId { get; private set; }

    public NotificationSeverity Severity { get; private set; }

    public string RuleKey { get; private set; }

    /// <summary>What this is about — an alert, an incident, an error group — so the dashboard can link to it.</summary>
    public NotificationSubject Subject { get; private set; }

    public Guid SubjectId { get; private set; }

    public string Title { get; private set; }

    public string Body { get; private set; }

    public NotificationDeliveryStatus Status { get; private set; }

    public int AttemptCount { get; private set; }

    /// <summary>When the dispatcher should next try. Backoff is expressed here rather than by sleeping.</summary>
    public DateTimeOffset NextAttemptAt { get; private set; }

    public DateTimeOffset? DeliveredAt { get; private set; }

    public string? LastError { get; private set; }

    /// <summary>Set when an in-app notification has been seen. Meaningless for the other kinds.</summary>
    public DateTimeOffset? ReadAt { get; private set; }

    private NotificationDelivery()
    {
        RuleKey = string.Empty;
        Title = string.Empty;
        Body = string.Empty;
    }

    private NotificationDelivery(
        Guid id,
        DateTimeOffset createdAt,
        Guid channelId,
        Guid? customerId,
        NotificationSeverity severity,
        string ruleKey,
        NotificationSubject subject,
        Guid subjectId,
        string title,
        string body)
        : base(id, createdAt)
    {
        ChannelId = channelId;
        CustomerId = customerId;
        Severity = severity;
        RuleKey = ruleKey;
        Subject = subject;
        SubjectId = subjectId;
        Title = title;
        Body = body;
        Status = NotificationDeliveryStatus.Pending;
        NextAttemptAt = createdAt;
    }

    public static NotificationDelivery Queue(
        Guid id,
        DateTimeOffset now,
        Guid channelId,
        Guid? customerId,
        NotificationSeverity severity,
        string ruleKey,
        NotificationSubject subject,
        Guid subjectId,
        string title,
        string body)
    {
        if (channelId == Guid.Empty)
        {
            throw DomainException.Validation("A delivery must name the channel it goes through.");
        }

        if (string.IsNullOrWhiteSpace(title))
        {
            throw DomainException.Validation("A notification must have a title.");
        }

        return new NotificationDelivery(
            id,
            now,
            channelId,
            customerId,
            severity,
            (ruleKey ?? string.Empty).Trim(),
            subject,
            subjectId,
            Clip(title, MaxSubjectLength),
            Clip(body ?? string.Empty, MaxBodyLength));
    }

    /// <summary>Marks the attempt as started, so a crashed dispatcher leaves evidence rather than a pristine row.</summary>
    public void BeginAttempt(DateTimeOffset now)
    {
        if (Status is NotificationDeliveryStatus.Delivered)
        {
            throw DomainException.Conflict("This notification has already been delivered.");
        }

        AttemptCount++;
        Status = NotificationDeliveryStatus.Sending;
        MarkUpdated(now);
    }

    public void MarkDelivered(DateTimeOffset now)
    {
        Status = NotificationDeliveryStatus.Delivered;
        DeliveredAt = now;
        LastError = null;
        MarkUpdated(now);
    }

    /// <summary>
    /// Records a failed attempt and schedules the next one, or gives up.
    ///
    /// Backoff is exponential and capped: the first retry is quick because most
    /// failures are transient, and the later ones are slow because the ones that
    /// are not transient must not become a denial-of-service against whoever is
    /// on the other end.
    /// </summary>
    public void MarkFailed(string error, int maxAttempts, TimeSpan baseDelay, TimeSpan maxDelay, DateTimeOffset now)
    {
        LastError = Clip(error ?? "Unknown error", 1000);

        if (AttemptCount >= maxAttempts)
        {
            Status = NotificationDeliveryStatus.Failed;
            MarkUpdated(now);

            return;
        }

        Status = NotificationDeliveryStatus.Pending;

        var factor = Math.Pow(2, Math.Min(AttemptCount - 1, 10));
        var delay = TimeSpan.FromSeconds(Math.Min(baseDelay.TotalSeconds * factor, maxDelay.TotalSeconds));

        NextAttemptAt = now + delay;
        MarkUpdated(now);
    }

    /// <summary>Never retried: nothing about the destination is going to change.</summary>
    public void Abandon(string reason, DateTimeOffset now)
    {
        Status = NotificationDeliveryStatus.Failed;
        LastError = Clip(reason, 1000);
        MarkUpdated(now);
    }

    public void MarkRead(DateTimeOffset now)
    {
        ReadAt ??= now;
        MarkUpdated(now);
    }

    public bool IsDue(DateTimeOffset now) =>
        Status is NotificationDeliveryStatus.Pending && NextAttemptAt <= now;

    private static string Clip(string value, int max)
    {
        var trimmed = value.Trim();

        return trimmed.Length <= max ? trimmed : trimmed[..max];
    }
}

public enum NotificationChannelKind
{
    /// <summary>Delivered into KNIGHT's own notification centre. Always available and never fails to reach a network.</summary>
    InApp = 0,
    Email = 1,
    Webhook = 2,
}

/// <summary>
/// Ordered least to most serious; <see cref="NotificationChannel.Accepts"/>
/// compares them directly.
/// </summary>
public enum NotificationSeverity
{
    Info = 0,
    Warning = 1,
    Critical = 2,
}

public enum NotificationSubject
{
    Alert = 0,
    Incident = 1,
    ErrorGroup = 2,
    Job = 3,
}

public enum NotificationDeliveryStatus
{
    Pending = 0,
    Sending = 1,
    Delivered = 2,
    Failed = 3,
}
