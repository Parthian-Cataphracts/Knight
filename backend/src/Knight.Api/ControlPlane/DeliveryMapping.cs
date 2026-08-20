using FeatureDelivery;
using FeatureDelivery.Domain;
using FeatureRegistry.Domain;
using Knight.Application.Abstractions.ControlPlane;
using Knight.Contracts.ControlPlane;

namespace Knight.Api.ControlPlane;

/// <summary>
/// Turns delivery aggregates into the contracts the dashboard reads.
///
/// The mapping lives here rather than on the contracts because
/// <c>Knight.Contracts</c> deliberately references nothing: it is the shape of
/// the API, and a DTO project that depends on the domain is a DTO project that
/// drags the domain into every consumer that ever generates a client from it.
///
/// One rule runs through all of it: a configuration secret has no mapping. There
/// is no response type here that can carry one, which is what makes "secrets are
/// never returned by a read API" true by construction rather than by every read
/// path remembering (docs/feature-delivery.md §9).
/// </summary>
internal static class DeliveryMapping
{
    public static FeatureVersionResponse ToResponse(this FeatureVersion version) => new()
    {
        Id = version.Id,
        FeatureId = version.FeatureId,
        Version = version.Version,
        Status = version.Status.ToString(),
        PackageReference = version.PackageReference,
        ArtifactDigest = version.ArtifactDigest,
        ArtifactSizeBytes = version.ArtifactSizeBytes,
        SigningKeyId = version.SigningKeyId,
        ReleaseNotes = version.ReleaseNotes,
        PublishedAt = version.PublishedAt,
        YankedAt = version.YankedAt,
        YankReason = version.YankReason,
        Dependencies = [.. version.Dependencies.Select(dependency =>
            new FeatureDependencyResponse(dependency.DependsOnSlug, dependency.VersionRangeExpression))],
    };

    /// <summary>
    /// Maps an installation, optionally with the labels only a join can supply.
    ///
    /// The labels are parameters rather than looked up here because a page of
    /// installations resolves them in one query for the whole page; doing it per
    /// row would turn one screen into fifty round trips.
    /// </summary>
    public static FeatureInstallationResponse ToResponse(
        this FeatureInstallation installation,
        string? storeName = null,
        string? featureName = null,
        bool entitled = false) => new()
    {
        Id = installation.Id,
        StoreId = installation.StoreId,
        FeatureId = installation.FeatureId,
        FeatureSlug = installation.FeatureSlug,
        FeatureName = featureName,
        StoreName = storeName,
        Entitled = entitled,
        IsEnabled = installation.State is InstallationState.Installed,
        LastTransitionAt = installation.UpdatedAt ?? installation.CreatedAt,
        State = installation.State.ToString(),
        InstalledVersion = installation.InstalledVersion,
        TargetVersion = installation.TargetVersion,
        PreviousVersion = installation.PreviousVersion,
        CurrentJobId = installation.CurrentJobId,
        FailureCode = installation.FailureCode,
        FailureMessage = installation.FailureMessage,
        RollbackOutcome = installation.RollbackOutcome.ToString(),
        BlockingReason = installation.BlockingReason,
        Health = installation.Health.ToString(),
        InstalledAt = installation.InstalledAt,
        DisabledAt = installation.DisabledAt,
        DataRetainedUntil = installation.DataRetainedUntil,
        RequiresManualIntervention = installation.RollbackOutcome is RollbackOutcome.ManualInterventionRequired,
    };

    public static FeatureJobResponse ToResponse(this FeatureInstallationJob job, string? storeName = null) => new()
    {
        Id = job.Id,
        StoreId = job.StoreId,
        FeatureId = job.FeatureId,
        FeatureSlug = job.FeatureSlug,
        StoreName = storeName,
        Type = job.Type.ToString(),
        State = job.State.ToString(),
        TargetVersion = job.TargetVersion,
        Trigger = job.Trigger.ToString(),
        CompletedStepCount = job.CompletedStepCount,
        TotalStepCount = job.TotalStepCount,
        AttemptCount = job.AttemptCount,
        MaxAttempts = job.MaxAttempts,
        FailureCode = job.FailureCode,
        FailureMessage = job.FailureMessage,
        RollbackOutcome = job.RollbackOutcome.ToString(),
        QueuedAt = job.QueuedAt,
        ClaimedAt = job.ClaimedAt,
        CompletedAt = job.CompletedAt,
        CorrelationId = job.CorrelationId,
    };

    public static FeatureJobDetailResponse ToDetailResponse(this FeatureInstallationJob job) => new(
        job.ToResponse(),
        [.. job.Steps
            .OrderBy(step => step.Sequence)
            .Select(step => new JobStepResponse(
                step.Sequence,
                step.Name,
                step.Status.ToString(),
                step.Output,
                step.ErrorCode,
                step.DurationMilliseconds,
                step.ReportCount,
                step.StartedAt,
                step.CompletedAt))]);

    public static FeaturePlanResponse ToResponse(this FeaturePlan plan) => new(
        plan.IsSuccessful,
        [.. plan.Steps.Select(step => new FeaturePlanStepResponse(
            step.FeatureId,
            step.VersionId,
            step.Slug,
            step.Name,
            step.Version,
            step.InstalledVersion,
            step.Action.ToString(),
            step.IsRoot,
            step.RequiredBy))],
        [.. plan.Failures.Select(failure =>
            new FeaturePlanFailureResponse(failure.Code, failure.Slug, failure.Message))]);

    public static InstallationRequestResponse ToResponse(this InstallationRequestResult result) => new(
        result.Plan.ToResponse(),
        [.. result.QueuedJobs.Select(job => job.ToResponse())],
        result.Installation.ToResponse());

    public static RolloutResponse ToResponse(this FeatureRollout rollout) => new()
    {
        Id = rollout.Id,
        FeatureId = rollout.FeatureId,
        FeatureSlug = rollout.FeatureSlug,
        TargetVersion = rollout.TargetVersion,
        State = rollout.State.ToString(),
        FailureThreshold = rollout.FailureThreshold,
        TotalStores = rollout.TotalStores,
        SucceededStores = rollout.SucceededStores,
        FailedStores = rollout.FailedStores,
        HaltReason = rollout.HaltReason,
        CreatedBy = rollout.CreatedBy,
        CreatedAt = rollout.CreatedAt,
        StartedAt = rollout.StartedAt,
        CompletedAt = rollout.CompletedAt,

        // Ordered explicitly. The waves are the sequence, and a UI that showed
        // them in whatever order the database returned would be showing the one
        // thing about a rollout that must not be guessed at.
        Waves = [.. rollout.Waves.OrderBy(wave => wave.Ordinal).Select(ToResponse)],
    };

    private static RolloutWaveResponse ToResponse(RolloutWave wave) => new()
    {
        Id = wave.Id,
        Ordinal = wave.Ordinal,
        IsCanary = wave.IsCanary,
        State = wave.State.ToString(),
        DispatchedAt = wave.DispatchedAt,
        CompletedAt = wave.CompletedAt,
        Targets =
        [
            .. wave.Targets.Select(target => new RolloutTargetResponse
            {
                StoreId = target.StoreId,
                State = target.State.ToString(),
                JobId = target.JobId,
                Detail = target.Detail,
                CompletedAt = target.CompletedAt,
            }),
        ],
    };
}
