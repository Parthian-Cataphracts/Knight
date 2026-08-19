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

    public static FeatureInstallationResponse ToResponse(this FeatureInstallation installation) => new()
    {
        Id = installation.Id,
        StoreId = installation.StoreId,
        FeatureId = installation.FeatureId,
        FeatureSlug = installation.FeatureSlug,
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

    public static FeatureJobResponse ToResponse(this FeatureInstallationJob job) => new()
    {
        Id = job.Id,
        StoreId = job.StoreId,
        FeatureId = job.FeatureId,
        FeatureSlug = job.FeatureSlug,
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
        [.. result.QueuedJobs.Select(ToResponse)],
        result.Installation.ToResponse());
}
