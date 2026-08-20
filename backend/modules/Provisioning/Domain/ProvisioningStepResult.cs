using Knight.Application.Abstractions.Observability;
using Knight.Domain.Common;
using Knight.Domain.Exceptions;

namespace Provisioning.Domain;

/// <summary>
/// Where one step of one provisioning job got to.
///
/// The detail is what a person reads when a store is stuck: "no agent has
/// enrolled on server X" rather than "waiting". It is redacted and capped like
/// every other operator-visible text KNIGHT stores — a provisioning step's
/// detail can quote whatever an agent or a store said, and a job record is read
/// long after the job by people who have no business seeing a secret.
/// </summary>
public sealed class ProvisioningStepResult : Entity
{
    public const int MaxDetailLength = 4000;

    public Guid JobId { get; private set; }

    public int Sequence { get; private set; }

    public string Name { get; private set; }

    public ProvisioningStepMode Mode { get; private set; }

    public ProvisioningStepStatus Status { get; private set; }

    public string? Detail { get; private set; }

    public string? ErrorCode { get; private set; }

    /// <summary>Who ticked a manual step off. Null for anything KNIGHT did itself.</summary>
    public Guid? CompletedBy { get; private set; }

    public DateTimeOffset StartedAt { get; private set; }

    public DateTimeOffset? CompletedAt { get; private set; }

    /// <summary>How many times this step has been reported — a waiting step is reported on every pass.</summary>
    public int ReportCount { get; private set; }

    private ProvisioningStepResult()
    {
        Name = string.Empty;
    }

    private ProvisioningStepResult(
        Guid id,
        Guid jobId,
        int sequence,
        string name,
        ProvisioningStepMode mode,
        ProvisioningStepStatus status,
        string? detail,
        string? errorCode,
        Guid? completedBy,
        DateTimeOffset now)
        : base(id)
    {
        JobId = jobId;
        Sequence = sequence;
        Name = name;
        Mode = mode;
        Status = status;
        Detail = Clean(detail);
        ErrorCode = string.IsNullOrWhiteSpace(errorCode) ? null : errorCode.Trim();
        CompletedBy = completedBy;
        StartedAt = now;
        CompletedAt = IsSettled(status) ? now : null;
        ReportCount = 1;
    }

    public static ProvisioningStepResult Create(
        Guid id,
        Guid jobId,
        int sequence,
        string name,
        ProvisioningStepMode mode,
        ProvisioningStepStatus status,
        string? detail,
        string? errorCode,
        Guid? completedBy,
        DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw DomainException.Validation("A step must have a name.");
        }

        return new ProvisioningStepResult(id, jobId, sequence, name.Trim(), mode, status, detail, errorCode, completedBy, now);
    }

    /// <summary>
    /// Replaces this step's outcome with a later report.
    ///
    /// A step that already succeeded is not downgraded by a later pass. The
    /// coordinator re-evaluates from the top, and a store whose agent enrolled
    /// and then went offline for an hour has not un-enrolled it — the later
    /// steps are where that shows up, not by rewriting history.
    /// </summary>
    public void Update(
        ProvisioningStepStatus status,
        string? detail,
        string? errorCode,
        Guid? completedBy,
        DateTimeOffset now)
    {
        ReportCount++;

        if (Status is ProvisioningStepStatus.Succeeded && status is not ProvisioningStepStatus.Pending)
        {
            return;
        }

        Status = status;
        Detail = Clean(detail) ?? (status is ProvisioningStepStatus.Pending ? null : Detail);
        ErrorCode = status is ProvisioningStepStatus.Pending
            ? null
            : string.IsNullOrWhiteSpace(errorCode) ? ErrorCode : errorCode.Trim();
        CompletedBy = completedBy ?? CompletedBy;
        CompletedAt = IsSettled(status) ? now : null;
    }

    private static bool IsSettled(ProvisioningStepStatus status) =>
        status is ProvisioningStepStatus.Succeeded or ProvisioningStepStatus.Failed or ProvisioningStepStatus.Skipped;

    private static string? Clean(string? detail)
    {
        if (string.IsNullOrWhiteSpace(detail))
        {
            return null;
        }

        var trimmed = (Redaction.Text(detail) ?? string.Empty).Trim();
        return trimmed.Length <= MaxDetailLength ? trimmed : trimmed[..MaxDetailLength];
    }
}

public enum ProvisioningStepStatus
{
    /// <summary>Not started, or cleared by a retry.</summary>
    Pending = 0,

    /// <summary>Under way, or waiting for a fact that has not happened yet.</summary>
    Waiting = 1,
    Succeeded = 2,
    Failed = 3,

    /// <summary>Not needed for this store — a shared-hosting store has no dedicated machine to build.</summary>
    Skipped = 4,
}
