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

    /// <summary>
    /// How the store wires the package into its runtime. Null only when the job
    /// carries no target version, which is every job that installs nothing.
    /// </summary>
    AgentRuntimeIntegration? Runtime,
    DateTimeOffset ClaimExpiresAt);

/// <summary>
/// The names a store needs to load a delivered package: what its migrations are
/// recorded under, what to load to get the code, and where to mount whatever it
/// serves.
///
/// Sent rather than inferred. A store that guesses the module name from the slug
/// is right only while the two happen to match, and after
/// <c>adr/0029</c> shortened every slug they no longer do — so the guess
/// silently registered an app that could not be imported.
///
/// Runtime-neutral since <c>adr/0032</c>: a Django store reads a namespace as an
/// app label and a module as an entry for INSTALLED_APPS, and a node store reads
/// the same two words as the ledger key and the specifier it imports. Only
/// <see cref="Runtime"/> tells them apart, and the store already knows which one
/// it is.
/// </summary>
public sealed record AgentRuntimeIntegration(
    string Runtime,
    string Namespace,
    string Module,
    string? MountExport,
    string? MountPrefix,
    IReadOnlyList<AgentWorker> Workers);

/// <summary>
/// One scheduled job, as the store is told about it.
///
/// The schedule is a word rather than a cron expression: the store decides what
/// "daily" means for it, which is the only party that can — it knows its own
/// timezone and when its quiet hours are.
/// </summary>
public sealed record AgentWorker(string Name, string Entrypoint, string Schedule);

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
/// <summary>
/// What the store is told about this version's database work.
///
/// <c>Extensions</c> are the database extensions the manifest declared. They
/// travel beside the migration policy rather than inside it and are created by
/// their own step before <c>migrate</c> runs: an extension is shared state the
/// Feature does not own, so it comes from a list KNIGHT closed at publish and is
/// never dropped again — not by a rollback and not by an uninstall
/// (docs/adr/0031-database-extensions-are-declared-not-migrated.md).
/// </summary>
public sealed record AgentMigrationPolicy(
    bool Required,
    bool Reversible,
    bool RequiresMaintenanceWindow,
    IReadOnlyList<string> Extensions);

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
