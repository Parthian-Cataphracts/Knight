namespace Observability.Domain;

/// <summary>
/// Persistence for error groups.
///
/// Implementations apply the caller's customer scope, so a customer principal
/// can neither read nor upsert another customer's groups.
/// </summary>
public interface IErrorGroupRepository
{
    Task<ErrorGroup?> GetByIdAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>
    /// The group for this fingerprint, if it exists. The single hottest read in
    /// the ingestion path — every error that arrives asks this question — which
    /// is why the fingerprint is indexed uniquely per store and version.
    /// </summary>
    Task<ErrorGroup?> FindByFingerprintAsync(
        Guid storeId,
        string fingerprint,
        int fingerprintVersion,
        CancellationToken cancellationToken);

    Task<IReadOnlyCollection<ErrorGroup>> FindByFingerprintsAsync(
        Guid storeId,
        IReadOnlyCollection<string> fingerprints,
        int fingerprintVersion,
        CancellationToken cancellationToken);

    Task<(IReadOnlyCollection<ErrorGroup> Items, long TotalCount)> ListAsync(
        Guid? storeId,
        ErrorGroupStatus? status,
        string? environment,
        int page,
        int pageSize,
        CancellationToken cancellationToken);

    /// <summary>
    /// Groups whose count has moved recently — the input to spike detection.
    /// Bounded by time rather than by page, because a sweep that only looked at
    /// the first page would miss exactly the spike that pushed something onto
    /// the second.
    /// </summary>
    Task<IReadOnlyCollection<ErrorGroup>> ListSeenSinceAsync(DateTimeOffset since, CancellationToken cancellationToken);

    Task AddAsync(ErrorGroup group, CancellationToken cancellationToken);

    Task SaveChangesAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Reads the raw events behind a group. Separate from the group repository
/// because the events live in the ingestion module's table: grouping annotates
/// that stream, it does not own it.
/// </summary>
public interface IErrorGroupEventReader
{
    Task<IReadOnlyCollection<ErrorGroupEventSample>> ListSamplesAsync(
        Guid groupId,
        int limit,
        CancellationToken cancellationToken);

    /// <summary>How many events the group has recorded within the window — what spike detection compares.</summary>
    Task<int> CountSinceAsync(Guid groupId, DateTimeOffset since, CancellationToken cancellationToken);
}

/// <summary>One kept occurrence, with the detail that makes a group actionable.</summary>
public sealed record ErrorGroupEventSample(
    Guid Id,
    DateTimeOffset OccurredAt,
    string? StoreVersion,
    string? RequestId,
    string? TraceId,
    string? StackTrace,
    string Message,
    string? Endpoint,
    int? StatusCode);

public interface IIncidentRepository
{
    Task<Incident?> GetByIdAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>Loads the aggregate with its timeline, for the detail screen and for appending.</summary>
    Task<Incident?> GetWithTimelineAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>The open incident this rule already raised for this subject, if any. What stops one outage becoming forty incidents.</summary>
    Task<Incident?> FindOpenByRuleAsync(string ruleKey, Guid subjectId, CancellationToken cancellationToken);

    Task<(IReadOnlyCollection<Incident> Items, long TotalCount)> ListAsync(
        IncidentStatus? status,
        IncidentSeverity? severity,
        Guid? storeId,
        bool openOnly,
        int page,
        int pageSize,
        CancellationToken cancellationToken);

    /// <summary>
    /// The next sequence number for a year's references. Allocated inside the
    /// same transaction as the insert so two incidents opened in the same second
    /// cannot share a reference.
    /// </summary>
    Task<int> NextReferenceSequenceAsync(int year, CancellationToken cancellationToken);

    Task AddAsync(Incident incident, CancellationToken cancellationToken);

    /// <summary>
    /// Marks timeline entries appended to an already-loaded incident as new.
    ///
    /// Needed because the domain assigns its own identifiers. Persistence infers
    /// "already exists" from a key being set, so an entry appended to a tracked
    /// aggregate would otherwise be written as an update to a row that has never
    /// existed — which fails loudly here, and would silently lose the timeline if
    /// it did not.
    /// </summary>
    void RegisterNewEvents(IReadOnlyCollection<IncidentEvent> entries);

    Task SaveChangesAsync(CancellationToken cancellationToken);
}

public interface INotificationRepository
{
    Task<NotificationChannel?> GetChannelAsync(Guid id, CancellationToken cancellationToken);

    Task<IReadOnlyCollection<NotificationChannel>> ListChannelsAsync(
        Guid? customerId,
        bool includeDisabled,
        CancellationToken cancellationToken);

    /// <summary>
    /// Every channel a notification for this customer could route to: the
    /// customer's own, plus the platform channels that watch everything.
    /// </summary>
    Task<IReadOnlyCollection<NotificationChannel>> ListRoutableAsync(Guid? customerId, CancellationToken cancellationToken);

    Task AddChannelAsync(NotificationChannel channel, CancellationToken cancellationToken);

    Task<NotificationDelivery?> GetDeliveryAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>Deliveries whose next attempt is due, oldest first, capped so one pass cannot run forever.</summary>
    Task<IReadOnlyCollection<NotificationDelivery>> ListDueAsync(DateTimeOffset now, int limit, CancellationToken cancellationToken);

    Task<(IReadOnlyCollection<NotificationDelivery> Items, long TotalCount)> ListDeliveriesAsync(
        Guid? channelId,
        NotificationDeliveryStatus? status,
        int page,
        int pageSize,
        CancellationToken cancellationToken);

    /// <summary>
    /// True when this exact notification has already been queued for this
    /// channel since the cutoff. Deduplication at the delivery layer as well as
    /// at the alert layer, because a rule that re-fires must not re-page.
    /// </summary>
    Task<bool> HasRecentAsync(
        Guid channelId,
        string ruleKey,
        Guid subjectId,
        DateTimeOffset since,
        CancellationToken cancellationToken);

    Task AddDeliveryAsync(NotificationDelivery delivery, CancellationToken cancellationToken);

    Task AddDeliveriesAsync(IReadOnlyCollection<NotificationDelivery> deliveries, CancellationToken cancellationToken);

    Task SaveChangesAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Actually sends. A port rather than an implementation because the module must
/// not know about HTTP clients, SMTP or the outbound address policy — and
/// because a test must be able to make a send fail on demand.
/// </summary>
public interface INotificationTransport
{
    /// <summary>
    /// Answers whether the notification left KNIGHT. A returned failure is
    /// retried; a thrown exception is treated identically but logged as a fault
    /// in the transport rather than in the destination.
    /// </summary>
    Task<NotificationSendResult> SendAsync(
        NotificationChannel channel,
        NotificationDelivery delivery,
        CancellationToken cancellationToken);
}

/// <summary>
/// The outcome of one send. <paramref name="Permanent"/> separates "try again"
/// from "this will never work": a webhook returning 404 does not deserve six
/// more attempts over the next hour.
/// </summary>
public sealed record NotificationSendResult(bool Succeeded, string? Error, bool Permanent = false)
{
    public static readonly NotificationSendResult Success = new(true, null);

    public static NotificationSendResult Transient(string error) => new(false, error);

    public static NotificationSendResult Fatal(string error) => new(false, error, true);
}
