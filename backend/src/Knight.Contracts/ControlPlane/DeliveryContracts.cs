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
    string RequiredBy);

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
    FeatureInstallationResponse Installation);

public sealed record FeatureJobResponse
{
    public required Guid Id { get; init; }

    public required Guid StoreId { get; init; }

    public required Guid FeatureId { get; init; }

    public required string FeatureSlug { get; init; }

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

public sealed record AgentMigrationResponse(
    bool Required,
    bool Reversible,
    bool RequiresMaintenanceWindow);

public sealed record AgentJobResponse(
    Guid JobId,
    string Type,
    string FeatureSlug,
    string? TargetVersion,
    string CorrelationId,
    IReadOnlyList<string> Steps,
    string? NextStep,
    AgentArtifactResponse? Artifact,
    AgentConfigurationResponse? Configuration,
    AgentMigrationResponse? Migrations,
    DateTimeOffset ClaimExpiresAt);
