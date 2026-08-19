namespace Knight.Application.Abstractions.ControlPlane;

/// <summary>
/// Assigns an incoming error to the problem it belongs to
/// ([`adr/0013`](../../../../docs/adr/0013-error-grouping-strategy.md)).
///
/// Declared here rather than referenced directly so that ingestion — the
/// highest-volume write path in KNIGHT — does not depend on the module that
/// analyses what it accepts. Ingestion's job is to accept telemetry and never
/// lose it; grouping is a consequence of that, never a precondition, and a
/// grouping failure must never turn into a rejected batch.
/// </summary>
public interface IErrorGrouping
{
    /// <summary>
    /// Groups a batch of accepted errors and answers, per event, which group it
    /// landed in and whether that event should be kept in full as a sample.
    ///
    /// A batch rather than one call per event because fifty errors from one
    /// store are usually two or three problems, and doing this row by row would
    /// make the hot path fifty round trips instead of two.
    /// </summary>
    Task<IReadOnlyCollection<ErrorGroupAssignment>> GroupAsync(
        IReadOnlyCollection<ErrorToGroup> events,
        CancellationToken cancellationToken);
}

/// <summary>One accepted error, in the only terms grouping needs.</summary>
public sealed record ErrorToGroup(
    Guid EventId,
    Guid StoreId,
    Guid CustomerId,
    string Environment,
    string ExceptionType,
    string Message,
    string? Endpoint,
    string? StackTrace,
    string? StoreVersion,
    DateTimeOffset OccurredAt);

/// <summary>
/// Where one event ended up. <paramref name="KeepSample"/> is false once a group
/// already holds enough full copies: the hundredth identical stack trace costs
/// storage and teaches nobody anything.
/// </summary>
public sealed record ErrorGroupAssignment(Guid EventId, Guid GroupId, bool KeepSample);

/// <summary>
/// An alert was raised, re-observed or resolved.
///
/// Published by whichever module detected the condition and consumed by
/// observability, which decides whether it deserves an incident and who needs to
/// be told. The detecting module stays ignorant of both questions — a fleet
/// sweep should not have opinions about notification routing.
/// </summary>
public sealed record AlertRaised(
    Guid AlertId,
    string RuleKey,
    string Severity,
    string Message,
    Guid SourceId,
    string Source,
    Guid? CustomerId,
    bool IsNew,
    DateTimeOffset OccurredAt);

/// <summary>The condition cleared. Notifying on recovery matters as much as notifying on failure.</summary>
public sealed record AlertResolved(
    Guid AlertId,
    string RuleKey,
    Guid SourceId,
    Guid? CustomerId,
    string Message,
    DateTimeOffset OccurredAt);

public interface IAlertEventPublisher
{
    Task PublishAsync(AlertRaised @event, CancellationToken cancellationToken);

    Task PublishAsync(AlertResolved @event, CancellationToken cancellationToken);
}

/// <summary>
/// Raises an alert from outside the module that owns alerting.
///
/// The delivery engine and the observability rule sweep both need to say "this
/// is wrong" without either of them owning the alert table or knowing about
/// deduplication. They describe the condition; the implementation decides
/// whether it is a new alert or another observation of an open one.
/// </summary>
public interface IAlertRaiser
{
    /// <summary>Answers the alert's id, and whether this call created it rather than re-observed one.</summary>
    Task<(Guid AlertId, bool IsNew)> RaiseAsync(
        string ruleKey,
        string severity,
        string source,
        Guid sourceId,
        Guid? customerId,
        string message,
        CancellationToken cancellationToken);

    /// <summary>Closes the open alert for this rule and source, if there is one. Silent when there is not.</summary>
    Task<bool> ResolveAsync(string ruleKey, Guid sourceId, CancellationToken cancellationToken);
}

/// <summary>
/// The facts the observability rules need about feature delivery, without a
/// module reference in either direction.
///
/// Every one of these is a *difference* between two records that live in
/// different modules — what a customer is entitled to versus what is installed,
/// what KNIGHT intended versus what the store reports on disk, when a job was
/// claimed versus now. None of them belongs to a single module, which is exactly
/// why they are read here rather than owned anywhere.
/// </summary>
public interface IDeliveryHealthReader
{
    /// <summary>
    /// Capabilities a customer is entitled to that are not installed on a store
    /// that should have them — the commercial promise and the running system
    /// having drifted apart (docs/feature-delivery.md §2).
    /// </summary>
    Task<IReadOnlyCollection<DeliveryDiscrepancy>> ListEntitledNotInstalledAsync(
        DateTimeOffset graceCutoff,
        CancellationToken cancellationToken);

    /// <summary>
    /// Installations whose reported version differs from the version KNIGHT
    /// believes it installed. Somebody changed the store by hand, or an install
    /// half-succeeded.
    /// </summary>
    Task<IReadOnlyCollection<DeliveryDiscrepancy>> ListDriftedAsync(CancellationToken cancellationToken);

    /// <summary>Jobs claimed by an agent that stopped reporting, past the point where waiting is still reasonable.</summary>
    Task<IReadOnlyCollection<DeliveryDiscrepancy>> ListStuckJobsAsync(
        DateTimeOffset cutoff,
        CancellationToken cancellationToken);

    /// <summary>Installation jobs that ended in failure and have not yet been alerted on.</summary>
    Task<IReadOnlyCollection<DeliveryDiscrepancy>> ListFailedJobsAsync(
        DateTimeOffset since,
        CancellationToken cancellationToken);
}

/// <summary>
/// One thing that is not as it should be. <paramref name="SubjectId"/> is what
/// alert deduplication keys on, so it must identify the *condition* — a store
/// and feature pair, a job — and not the observation.
/// </summary>
public sealed record DeliveryDiscrepancy(
    Guid SubjectId,
    Guid StoreId,
    Guid CustomerId,
    string StoreName,
    string FeatureSlug,
    string Detail);

/// <summary>
/// Pushes a change to whoever is watching the dashboard right now.
///
/// A port so that nothing in the modules depends on SignalR, and so that a host
/// running without the hub — a test, a background worker — simply drops the
/// broadcast instead of failing the operation that triggered it. Realtime is an
/// improvement on polling, never a thing correctness depends on.
/// </summary>
public interface IRealtimeNotifier
{
    /// <summary>
    /// Sends to everyone entitled to see it. The implementation decides who that
    /// is from the customer id; callers never name a connection or a group,
    /// because a caller that could name a group could address someone else's.
    /// </summary>
    Task BroadcastAsync(RealtimeMessage message, CancellationToken cancellationToken);
}

/// <summary>
/// One realtime message. <paramref name="CustomerId"/> null means platform-only:
/// it reaches platform principals and nobody else, never "everyone".
/// </summary>
public sealed record RealtimeMessage(string Event, Guid? CustomerId, object Payload);
