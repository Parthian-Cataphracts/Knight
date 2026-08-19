using Knight.Domain.Common;
using Knight.Domain.Exceptions;

namespace FeatureDelivery.Domain;

/// <summary>
/// What happened at one step of one job.
///
/// The output is capped and the record carries no secret: configuration values a
/// step applied are referenced by name, never by value. A job record is read by
/// support staff, exported into incidents, and kept long after the job — it is
/// the last place a decrypted secret should be able to come to rest
/// (docs/feature-delivery.md §9).
/// </summary>
public sealed class JobStepResult : Entity
{
    public const int MaxOutputLength = 8000;

    public Guid JobId { get; private set; }

    /// <summary>Position in the pipeline, so the dashboard can render "3 of 10" without knowing the pipeline.</summary>
    public int Sequence { get; private set; }

    public string Name { get; private set; }

    public StepStatus Status { get; private set; }

    /// <summary>The step's own output, truncated. Useful for a migration's summary or a health check's detail.</summary>
    public string? Output { get; private set; }

    /// <summary>A machine-readable failure code, so alerting can branch without parsing prose.</summary>
    public string? ErrorCode { get; private set; }

    public int? DurationMilliseconds { get; private set; }

    public DateTimeOffset StartedAt { get; private set; }

    public DateTimeOffset? CompletedAt { get; private set; }

    /// <summary>
    /// How many times the agent reported this step. A step that succeeded on its
    /// third report is a step worth looking at even though the job passed.
    /// </summary>
    public int ReportCount { get; private set; }

    private JobStepResult()
    {
        Name = string.Empty;
    }

    private JobStepResult(
        Guid id,
        Guid jobId,
        int sequence,
        string name,
        StepStatus status,
        string? output,
        string? errorCode,
        int? durationMilliseconds,
        DateTimeOffset now)
        : base(id)
    {
        JobId = jobId;
        Sequence = sequence;
        Name = name;
        Status = status;
        Output = Truncate(output);
        ErrorCode = errorCode;
        DurationMilliseconds = durationMilliseconds;
        StartedAt = now;
        CompletedAt = status is StepStatus.Running ? null : now;
        ReportCount = 1;
    }

    public static JobStepResult Create(
        Guid id,
        Guid jobId,
        int sequence,
        string name,
        StepStatus status,
        string? output,
        string? errorCode,
        int? durationMilliseconds,
        DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw DomainException.Validation("A step must have a name.");
        }

        return new JobStepResult(
            id,
            jobId,
            sequence,
            name.Trim(),
            status,
            output,
            string.IsNullOrWhiteSpace(errorCode) ? null : errorCode.Trim(),
            durationMilliseconds,
            now);
    }

    /// <summary>
    /// Replaces this step's outcome with a later report of the same step.
    ///
    /// A step that already succeeded is never downgraded by a repeat report. The
    /// case is an agent that succeeded, lost the network before its reply landed,
    /// and re-ran a step that had in fact already applied — treating its second,
    /// now-redundant attempt as a failure would fail a job that worked.
    /// </summary>
    public void Update(StepStatus status, string? output, string? errorCode, int? durationMilliseconds, DateTimeOffset now)
    {
        ReportCount++;

        if (Status is StepStatus.Succeeded && status is not StepStatus.Succeeded)
        {
            return;
        }

        Status = status;
        Output = Truncate(output) ?? Output;
        ErrorCode = string.IsNullOrWhiteSpace(errorCode) ? ErrorCode : errorCode.Trim();
        DurationMilliseconds = durationMilliseconds ?? DurationMilliseconds;
        CompletedAt = status is StepStatus.Running ? null : now;
    }

    private static string? Truncate(string? output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return null;
        }

        var trimmed = output.Trim();
        return trimmed.Length <= MaxOutputLength ? trimmed : trimmed[..MaxOutputLength];
    }
}

public enum StepStatus
{
    Running = 0,
    Succeeded = 1,
    Failed = 2,

    /// <summary>Not needed for this job — a migration step where the manifest declares no migrations.</summary>
    Skipped = 3,
}
