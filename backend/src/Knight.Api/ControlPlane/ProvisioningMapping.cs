using Knight.Contracts.ControlPlane;
using Provisioning.Domain;
using Stores.Domain;

namespace Knight.Api.ControlPlane;

/// <summary>
/// Turns provisioning runs and backup reports into what the dashboard reads.
///
/// The step detail is carried through as written, because it is the whole value
/// of the screen: "waiting for the agent on the store's server to enrol" tells an
/// operator what to do, and "Waiting" does not.
/// </summary>
internal static class ProvisioningMapping
{
    public static ProvisioningJobResponse ToResponse(this ProvisioningJob job) => new()
    {
        Id = job.Id,
        StoreId = job.StoreId,
        CustomerId = job.CustomerId,
        Kind = job.Kind.ToString(),
        State = job.State.ToString(),
        AwaitingOperator = job.IsAwaitingOperator,
        CurrentStep = job.NextStep(),
        CompletedStepCount = job.CompletedStepCount,
        TotalStepCount = job.TotalStepCount,
        BaseImageVersion = job.BaseImageVersion,
        RetainUntil = job.RetainUntil,
        FailureCode = job.FailureCode,
        FailureMessage = job.FailureMessage,
        CreatedAt = job.CreatedAt,
        CompletedAt = job.CompletedAt,

        // Ordered by the pipeline's own sequence rather than by when each row
        // happened to be written: a run that waited on step five for an hour
        // still reads top to bottom.
        Steps = [.. job.Steps.OrderBy(step => step.Sequence).Select(ToResponse)],
    };

    public static ProvisioningStepResponse ToResponse(this ProvisioningStepResult step) => new()
    {
        Sequence = step.Sequence,
        Name = step.Name,
        Mode = step.Mode.ToString(),
        Status = step.Status.ToString(),
        Detail = step.Detail,
        ErrorCode = step.ErrorCode,
        CompletedBy = step.CompletedBy,
        StartedAt = step.StartedAt,
        CompletedAt = step.CompletedAt,
    };

    public static StoreBackupResponse ToResponse(this StoreBackup backup) => new()
    {
        Id = backup.Id,
        StoreId = backup.StoreId,
        Status = backup.Status.ToString(),
        Kind = backup.Kind.ToString(),
        StartedAt = backup.StartedAt,
        CompletedAt = backup.CompletedAt,
        ReportedAt = backup.ReportedAt,
        SizeBytes = backup.SizeBytes,
        Location = backup.Location,
        Detail = backup.Detail,
        DurationSeconds = backup.Duration is { } duration ? (int)duration.TotalSeconds : null,
    };
}
