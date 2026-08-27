namespace Knight.Contracts.ControlPlane;

// --- Requests ---------------------------------------------------------------

public sealed record ManifestValidationRequest(string Manifest);

public sealed record CreateFeatureVersionRequest(
    string Manifest,
    string PackageReference,
    string ArtifactDigest,
    string Signature,
    string? SigningKeyId,
    string? ReleaseNotes);

public sealed record YankVersionRequest(string Reason);

public sealed record InstallFeatureRequest(Guid StoreId, string Slug, string? VersionRange);

public sealed record InstallationActionRequest(Guid StoreId, Guid FeatureId, string? Reason);

/// <summary>
/// A configuration update. Secrets arrive here in the clear — this is the one
/// direction they travel — and are encrypted before they are stored. They are
/// never echoed back by any read path.
/// </summary>
public sealed record ConfigureFeatureRequest(
    Guid StoreId,
    Guid FeatureId,
    string Values,
    IReadOnlyDictionary<string, string>? Secrets);

// --- Responses ---------------------------------------------------------------

public sealed record ManifestErrorResponse(string Path, string Message);

public sealed record ManifestValidationResponse(
    bool IsValid,
    string? Slug,
    string? Version,
    IReadOnlyList<ManifestErrorResponse> Errors);

public sealed record SigningKeyRevocationResponse(string KeyId, int YankedVersions);

/// <summary>
/// A published version as the dashboard sees it.
///
/// The digest and signature are included deliberately: an operator investigating
/// an install failure needs to be able to compare what KNIGHT believes it
/// published against what a store reports it received, and hiding the values
/// would only mean doing that comparison through a database console.
/// </summary>
public sealed record FeatureVersionResponse
{
    public required Guid Id { get; init; }

    public required Guid FeatureId { get; init; }

    public required string Version { get; init; }

    public required string Status { get; init; }

    public required string PackageReference { get; init; }

    public required string ArtifactDigest { get; init; }

    public required long ArtifactSizeBytes { get; init; }

    public required string SigningKeyId { get; init; }

    public string? ReleaseNotes { get; init; }

    public DateTimeOffset? PublishedAt { get; init; }

    public DateTimeOffset? YankedAt { get; init; }

    public string? YankReason { get; init; }

    public required IReadOnlyList<FeatureDependencyResponse> Dependencies { get; init; }
}

public sealed record FeatureDependencyResponse(string Slug, string VersionRange);

/// <summary>
/// An installation. Entitlement is deliberately not a field here: it is a
/// separate fact with a separate lifecycle, and the dashboard shows the two as
/// separate columns precisely so their disagreements stay visible
/// (docs/feature-delivery.md §2).
/// </summary>
public sealed record FeatureInstallationResponse
{
    public required Guid Id { get; init; }

    public required Guid StoreId { get; init; }

    public required Guid FeatureId { get; init; }

    public required string FeatureSlug { get; init; }

    /// <summary>Resolved server-side: the client cannot join a feature id to its name.</summary>
    public string? FeatureName { get; init; }

    public string? StoreName { get; init; }

    /// <summary>
    /// Whether the customer is entitled to this capability.
    ///
    /// Entitlement and installation are separate facts and the screen shows them
    /// as separate columns, so the difference between "owed but missing" and
    /// "installed but no longer paid for" is visible rather than inferred
    /// (docs/adr/0019-entitlement-as-an-explicit-record.md).
    /// </summary>
    public required bool Entitled { get; init; }

    /// <summary>True while the feature is installed and serving, as opposed to installed and switched off.</summary>
    public required bool IsEnabled { get; init; }

    /// <summary>When the installation last changed state, so a list can be ordered by what moved recently.</summary>
    public required DateTimeOffset LastTransitionAt { get; init; }

    public required string State { get; init; }

    public string? InstalledVersion { get; init; }

    public string? TargetVersion { get; init; }

    public string? PreviousVersion { get; init; }

    public Guid? CurrentJobId { get; init; }

    public string? FailureCode { get; init; }

    public string? FailureMessage { get; init; }

    public required string RollbackOutcome { get; init; }

    /// <summary>Why an entitled feature is not installed. Null when nothing is blocking it.</summary>
    public string? BlockingReason { get; init; }

    public required string Health { get; init; }

    public DateTimeOffset? InstalledAt { get; init; }

    public DateTimeOffset? DisabledAt { get; init; }

    public DateTimeOffset? DataRetainedUntil { get; init; }

    /// <summary>
    /// True when a person has to intervene before this store is well again. The
    /// dashboard surfaces it as a notice rather than a status chip, because it
    /// means a database is in a state KNIGHT refused to guess about.
    /// </summary>
    public required bool RequiresManualIntervention { get; init; }
}

public sealed record FeaturePlanStepResponse(
    Guid FeatureId,
    Guid VersionId,
    string Slug,
    string Name,
    string Version,
    string? InstalledVersion,
    string Action,
    bool IsRoot,
    string RequiredBy,
    bool MigrationsRequired,
    bool MigrationsReversible,
    int MigrationSeconds,
    bool RequiresRestart);

public sealed record FeaturePlanFailureResponse(string Code, string Slug, string Message);

public sealed record FeaturePlanResponse(
    bool IsSuccessful,
    IReadOnlyList<FeaturePlanStepResponse> Steps,
    IReadOnlyList<FeaturePlanFailureResponse> Failures);

/// <summary>
/// The answer to an install request: the plan, and the jobs it produced.
///
/// A request that produced no jobs is still a successful response — the plan
/// explains why — because "we could not install this, and here is the constraint
/// that stopped us" is an answer, not an error.
/// </summary>
public sealed record InstallationRequestResponse(
    FeaturePlanResponse Plan,
    IReadOnlyList<FeatureJobResponse> Jobs,

    /// <summary>
    /// The store's row for this Feature, or null when planning failed before one
    /// could exist.
    ///
    /// Null is the honest answer for a store being refused a Feature it has never
    /// had: there is nothing installed and nothing to describe. The plan above
    /// carries the reasons, which is what a caller should be reading in that
    /// case anyway.
    /// </summary>
    FeatureInstallationResponse? Installation);

public sealed record FeatureJobResponse
{
    public required Guid Id { get; init; }

    public required Guid StoreId { get; init; }

    public required Guid FeatureId { get; init; }

    public required string FeatureSlug { get; init; }

    public string? StoreName { get; init; }

    public required string Type { get; init; }

    public required string State { get; init; }

    public string? TargetVersion { get; init; }

    public required string Trigger { get; init; }

    public required int CompletedStepCount { get; init; }

    public required int TotalStepCount { get; init; }

    public required int AttemptCount { get; init; }

    public required int MaxAttempts { get; init; }

    public string? FailureCode { get; init; }

    public string? FailureMessage { get; init; }

    public required string RollbackOutcome { get; init; }

    public required DateTimeOffset QueuedAt { get; init; }

    public DateTimeOffset? ClaimedAt { get; init; }

    public DateTimeOffset? CompletedAt { get; init; }

    public required string CorrelationId { get; init; }
}

public sealed record JobStepResponse(
    int Sequence,
    string Name,
    string Status,
    string? Output,
    string? ErrorCode,
    int? DurationMilliseconds,
    int ReportCount,
    DateTimeOffset StartedAt,
    DateTimeOffset? CompletedAt);

public sealed record FeatureJobDetailResponse(FeatureJobResponse Job, IReadOnlyList<JobStepResponse> Steps);

// --- The agent channel --------------------------------------------------------

public sealed record AgentStepReportRequest(
    string Step,
    string Status,
    string? Output,
    string? ErrorCode,
    int? DurationMilliseconds);

public sealed record AgentJobCompletionRequest(
    bool Succeeded,
    string? FailureCode,
    string? FailureMessage,
    string? RollbackOutcome,
    string? InstalledVersion,
    string? Health);

/// <summary>
/// Everything the store needs to verify an artifact before it installs it. The
/// URL is minted per hand-out and expires; the digest and signature let the store
/// check the bytes without trusting the channel that delivered them.
/// </summary>
public sealed record AgentArtifactResponse(
    string PackageReference,
    string Digest,
    long SizeBytes,
    string Signature,
    string SigningKeyId,
    string DownloadUrl,
    DateTimeOffset DownloadUrlExpiresAt);

/// <summary>
/// The configuration to apply. This is the only place a secret value travels, and
/// only ever to the one store that needs it.
/// </summary>
public sealed record AgentConfigurationResponse(
    int Version,
    string Values,
    IReadOnlyDictionary<string, string> Secrets);

/// <summary>
/// The database work this version needs, as the store's agent is told about it.
///
/// <c>Extensions</c> are created by their own step before the migrations run and
/// are never dropped again: an extension is shared with the store and with every
/// other Feature installed in the same database, so a rollback that removed one
/// could break a Feature it has never heard of
/// (docs/adr/0031-database-extensions-are-declared-not-migrated.md).
/// </summary>
public sealed record AgentMigrationResponse(
    bool Required,
    bool Reversible,
    bool RequiresMaintenanceWindow,
    IReadOnlyList<string> Extensions);

/// <summary>
/// How the store wires the delivered package into its runtime: the module that
/// goes into INSTALLED_APPS, the label its migrations are recorded under, and
/// the urlconf to mount and where.
///
/// Sent rather than left to the store to infer from the slug. The two match only
/// by coincidence, and after <c>adr/0029</c> shortened every slug they stopped
/// matching - at which point a store guessing registered an app it could not
/// import, and a Feature declaring URLs served none of them.
/// </summary>
public sealed record AgentDjangoResponse(
    string AppLabel,
    string InstalledApp,
    string? UrlInclude,
    string? UrlPrefix,
    IReadOnlyList<AgentWorkerResponse> Workers);

/// <summary>
/// The same three facts, in the words every runtime shares: what this Feature's
/// migrations are recorded under, what the store loads to get the code, and
/// where whatever it serves is mounted (<c>adr/0032</c> §3).
///
/// A Django store reads <see cref="Namespace"/> as an app label and
/// <see cref="Module"/> as an entry for INSTALLED_APPS; a node store reads the
/// same two words as its ledger key and the specifier it imports. The store
/// already knows which it is, so <see cref="Runtime"/> is there to be checked
/// rather than to be branched on: a store handed a package for a runtime it does
/// not run should refuse the job, not improvise.
/// </summary>
public sealed record AgentRuntimeResponse(
    string Runtime,
    string Namespace,
    string Module,
    string? MountExport,
    string? MountPrefix,
    IReadOnlyList<AgentWorkerResponse> Workers);

/// <summary>
/// A scheduled job the store must run once the Feature is installed.
///
/// Sent with the install rather than configured per store, so that installing a
/// Feature installs its schedule too. A worker wired up by hand on every store
/// is a worker that does nothing on the stores where somebody forgot.
/// </summary>
public sealed record AgentWorkerResponse(string Name, string Entrypoint, string Schedule);

public sealed record AgentJobResponse(
    Guid JobId,
    string Type,
    string FeatureSlug,
    string? TargetVersion,
    string CorrelationId,

    /// <summary>
    /// The W3C traceparent of the request that queued the job, so an agent's own
    /// spans join that trace rather than starting a disconnected one. Optional:
    /// an agent that does not trace ignores a string.
    /// </summary>
    string? TraceParent,
    IReadOnlyList<string> Steps,
    string? NextStep,
    AgentArtifactResponse? Artifact,
    AgentConfigurationResponse? Configuration,
    AgentMigrationResponse? Migrations,
    /// <summary>
    /// Django-shaped wiring, sent only when the runtime is django.
    ///
    /// **Deprecated since <c>adr/0032</c>** in favour of <see cref="Runtime"/>,
    /// and still sent because a store is upgraded on its own schedule and a
    /// staged rollout deliberately leaves some behind. Dropping it would break
    /// the stores that had not caught up at the exact moment they were being
    /// asked to install something. It comes out when no supported store reads it.
    /// </summary>
    AgentDjangoResponse? Django,

    /// <summary>
    /// How the store wires the package in, whatever it runs. Null only when the
    /// job carries no target version, which is every job that installs nothing.
    /// </summary>
    AgentRuntimeResponse? Runtime,
    DateTimeOffset ClaimExpiresAt);

// --- Staged rollouts -------------------------------------------------------

/// <summary>
/// Asks for a rollout of one Feature version across the fleet.
///
/// <see cref="WavePercentages"/> describes the waves *after* the canary, which is
/// always exactly one store. Empty means everything else goes in one wave, which
/// is reasonable for a handful of stores and reckless for hundreds.
/// </summary>
public sealed record PlanRolloutRequest(
    string Slug,
    string Version,
    IReadOnlyList<int>? WavePercentages,
    int? FailureThreshold,
    IReadOnlyList<Guid>? StoreIds,
    Guid? CanaryStoreId);

public sealed record RolloutActionRequest(string Reason);

public sealed record RolloutTargetResponse
{
    public required Guid StoreId { get; init; }

    public required string State { get; init; }

    public Guid? JobId { get; init; }

    public string? Detail { get; init; }

    public DateTimeOffset? CompletedAt { get; init; }
}

public sealed record RolloutWaveResponse
{
    public required Guid Id { get; init; }

    public required int Ordinal { get; init; }

    /// <summary>True for wave 0, the single store an unproven version reaches first.</summary>
    public required bool IsCanary { get; init; }

    public required string State { get; init; }

    public DateTimeOffset? DispatchedAt { get; init; }

    public DateTimeOffset? CompletedAt { get; init; }

    public required RolloutTargetResponse[] Targets { get; init; }
}

public sealed record RolloutResponse
{
    public required Guid Id { get; init; }

    public required Guid FeatureId { get; init; }

    public required string FeatureSlug { get; init; }

    public required string TargetVersion { get; init; }

    public required string State { get; init; }

    public required int FailureThreshold { get; init; }

    public required int TotalStores { get; init; }

    public required int SucceededStores { get; init; }

    public required int FailedStores { get; init; }

    /// <summary>Why a halted rollout halted, so the dashboard can say so rather than showing a stopped bar.</summary>
    public string? HaltReason { get; init; }

    public required string CreatedBy { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset? StartedAt { get; init; }

    public DateTimeOffset? CompletedAt { get; init; }

    public required RolloutWaveResponse[] Waves { get; init; }
}
