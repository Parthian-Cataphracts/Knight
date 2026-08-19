using FeatureDelivery.Domain;

namespace FeatureDelivery;

/// <summary>
/// One job, as handed to the agent that will carry it out.
///
/// The download URL is minted for this hand-out and expires; it is never stored
/// and never reused. The digest and signature travel with it so the store can
/// verify the bytes it receives without trusting the channel that delivered them
/// (docs/adr/0015-feature-delivery-mechanism.md).
/// </summary>
public sealed record AgentJobAssignment(
    Guid JobId,
    string Type,
    string FeatureSlug,
    string? TargetVersion,
    string CorrelationId,

    /// <summary>
    /// The W3C traceparent of the request that queued this job. An agent that
    /// understands it continues that trace; one that does not ignores a string,
    /// which is why it is optional and carried rather than negotiated.
    /// </summary>
    string? TraceParent,
    IReadOnlyList<string> Steps,
    string? NextStep,
    AgentArtifact? Artifact,
    AgentConfiguration? Configuration,
    AgentMigrationPolicy? Migrations,
    DateTimeOffset ClaimExpiresAt);

public sealed record AgentArtifact(
    string PackageReference,
    string Digest,
    long SizeBytes,
    string Signature,
    string SigningKeyId,
    Uri DownloadUrl,
    DateTimeOffset DownloadUrlExpiresAt);

/// <summary>
/// The configuration to apply. Secret values are present here and only here —
/// this payload travels over the authenticated job channel to one store, and is
/// never written to a job record, a log or an audit entry.
/// </summary>
public sealed record AgentConfiguration(
    int Version,
    string ValuesJson,
    IReadOnlyDictionary<string, string> Secrets);

/// <summary>
/// What the agent needs to know before it touches the database: whether there
/// are migrations, and whether it will be able to undo them if a later step
/// fails.
/// </summary>
public sealed record AgentMigrationPolicy(bool Required, bool Reversible, bool RequiresMaintenanceWindow);

public sealed record StepReport(
    string Step,
    string Status,
    string? Output,
    string? ErrorCode,
    int? DurationMilliseconds);

public sealed record JobCompletionReport(
    bool Succeeded,
    string? FailureCode,
    string? FailureMessage,
    string? RollbackOutcome,
    string? InstalledVersion,
    string? Health);

/// <summary>
/// The whole of what a store's agent may ask KNIGHT to do.
///
/// Four operations, and no fifth. The agent polls for its own store's next job,
/// claims it, reports steps, and reports the outcome. There is no endpoint here
/// that takes a command, a path or a script, which is what keeps a compromised
/// control plane from becoming arbitrary code execution across every store at
/// once (docs/feature-delivery.md §15).
/// </summary>
public interface IAgentJobService
{
    /// <summary>
    /// Hands the store its next job and claims it, or returns null when there is
    /// nothing to do. Claiming on hand-out rather than in a separate call means
    /// there is no window in which two agents both believe they hold the job.
    /// </summary>
    Task<AgentJobAssignment?> ClaimNextAsync(Guid storeId, CancellationToken cancellationToken);

    Task ReportStepAsync(Guid storeId, Guid jobId, StepReport report, CancellationToken cancellationToken);

    Task CompleteAsync(Guid storeId, Guid jobId, JobCompletionReport report, CancellationToken cancellationToken);

    /// <summary>
    /// Returns jobs whose agent stopped reporting to the queue, or fails them
    /// once they have used up their attempts. Run on a timer by the host.
    /// </summary>
    Task<int> SweepExpiredClaimsAsync(CancellationToken cancellationToken);
}
