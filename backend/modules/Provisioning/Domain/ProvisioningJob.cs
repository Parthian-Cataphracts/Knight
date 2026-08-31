using Knight.Domain.Common;
using Knight.Domain.Exceptions;

namespace Provisioning.Domain;

/// <summary>
/// The path from "a customer signed up" to "a working store with its base
/// Features installed and healthy" — and, in the other direction, from a live
/// store to purged data (docs/store-provisioning.md §1).
///
/// The job is a record of steps, not a status field, for the same reason a
/// feature installation job is: "provisioning failed" is not something anybody
/// can act on. Which step, what it was waiting for, and whether the one before
/// it had already issued credentials are the difference between retrying and
/// picking up a half-built store by hand.
///
/// Steps KNIGHT does not automate are represented rather than pretended away
/// (docs/store-provisioning.md §2). Creating the VM and wiring DNS are manual
/// today; they appear as manual steps an operator ticks off, so the state
/// machine is already correct while the automation is not yet written.
/// </summary>
public sealed class ProvisioningJob : AuditableEntity, ICustomerOwned
{
    public Guid CustomerId { get; private set; }

    public Guid StoreId { get; private set; }

    public ProvisioningKind Kind { get; private set; }

    public ProvisioningState State { get; private set; }

    /// <summary>
    /// The caller's key for this request, so a retried creation returns the job
    /// it already made instead of starting a second provisioning run against the
    /// same store.
    /// </summary>
    public string IdempotencyKey { get; private set; }

    public string CorrelationId { get; private set; }

    /// <summary>
    /// The base store image this store was built from. Recorded because Feature
    /// compatibility ranges are checked against the store version the image
    /// pins, so "which image" is a question an incident will ask
    /// (docs/store-provisioning.md §3).
    /// </summary>
    public string? BaseImageVersion { get; private set; }

    /// <summary>
    /// For a deprovisioning job: the moment the contractual retention window
    /// closes and the store's data may be purged. Null on a provisioning job.
    /// </summary>
    public DateTimeOffset? RetainUntil { get; private set; }

    public Guid RequestedBy { get; private set; }

    public DateTimeOffset? CompletedAt { get; private set; }

    public string? FailureCode { get; private set; }

    public string? FailureMessage { get; private set; }

    /// <summary>
    /// How the coordinator should treat the last failure — retry it, give up, or
    /// escalate to a person (docs/self-service-saas-plan.md §8). Null while the
    /// job has never failed.
    /// </summary>
    public ProvisioningFailureClass? FailureClass { get; private set; }

    /// <summary>
    /// How many times this job has been started or retried, so an automated
    /// coordinator can stop after a bounded number of attempts rather than retry
    /// forever. One on creation; incremented by <see cref="Retry"/>.
    /// </summary>
    public int AttemptCount { get; private set; }

    private readonly List<ProvisioningStepResult> _steps = [];

    public IReadOnlyCollection<ProvisioningStepResult> Steps => _steps.AsReadOnly();

    public int TotalStepCount => ProvisioningPipeline.StepsFor(Kind).Count;

    public int CompletedStepCount => _steps.Count(step => step.Status is ProvisioningStepStatus.Succeeded or ProvisioningStepStatus.Skipped);

    private ProvisioningJob()
    {
        IdempotencyKey = string.Empty;
        CorrelationId = string.Empty;
    }

    private ProvisioningJob(
        Guid id,
        DateTimeOffset createdAt,
        Guid customerId,
        Guid storeId,
        ProvisioningKind kind,
        string idempotencyKey,
        string correlationId,
        Guid requestedBy)
        : base(id, createdAt)
    {
        CustomerId = customerId;
        StoreId = storeId;
        Kind = kind;
        IdempotencyKey = idempotencyKey;
        CorrelationId = correlationId;
        RequestedBy = requestedBy;
        State = ProvisioningState.Running;
        AttemptCount = 1;
    }

    public static ProvisioningJob Start(
        Guid id,
        DateTimeOffset now,
        Guid customerId,
        Guid storeId,
        ProvisioningKind kind,
        string idempotencyKey,
        string correlationId,
        Guid requestedBy)
    {
        if (customerId == Guid.Empty || storeId == Guid.Empty)
        {
            throw DomainException.Validation("A provisioning job must name a customer and a store.");
        }

        return new ProvisioningJob(
            id,
            now,
            customerId,
            storeId,
            kind,
            RequireText(idempotencyKey, "idempotency key", 200),
            RequireText(correlationId, "correlation id", 100),
            requestedBy);
    }

    /// <summary>Records which base image the store instance was built from.</summary>
    public void RecordBaseImage(string version, DateTimeOffset now)
    {
        BaseImageVersion = RequireText(version, "base image version", 50);
        MarkUpdated(now);
    }

    /// <summary>
    /// Sets the moment a deprovisioned store's data may be purged. The window is
    /// contractual, so it is stored on the job rather than recomputed later: the
    /// plan a customer was on when they left is not the plan the catalogue will
    /// hold a year from now.
    /// </summary>
    public void RetainDataUntil(DateTimeOffset retainUntil, DateTimeOffset now)
    {
        if (Kind is not ProvisioningKind.Deprovision)
        {
            throw DomainException.Conflict("Only a deprovisioning job has a retention window.");
        }

        RetainUntil = retainUntil;
        MarkUpdated(now);
    }

    /// <summary>
    /// The step the job is on, or null when every step has finished. This is
    /// what makes the job resumable: a run that was interrupted asks what
    /// remains rather than starting at the top and re-issuing credentials.
    /// </summary>
    public string? NextStep()
    {
        foreach (var step in ProvisioningPipeline.StepsFor(Kind))
        {
            var recorded = Find(step.Name);

            if (recorded is null ||
                recorded.Status is not (ProvisioningStepStatus.Succeeded or ProvisioningStepStatus.Skipped))
            {
                return step.Name;
            }
        }

        return null;
    }

    /// <summary>
    /// Records where a step got to.
    ///
    /// Reporting the same step twice is normal rather than an error: the
    /// coordinator re-evaluates a waiting step every time it runs, and a step
    /// that is still waiting for an agent to enrol reports "waiting" on every
    /// pass until it is not.
    /// </summary>
    /// <returns>
    /// The step row when this report created one, so the caller can register it
    /// as an insert — a child added to a loaded aggregate is not reliably
    /// classified by the change tracker when the key comes from the domain.
    /// </returns>
    public ProvisioningStepResult? ReportStep(
        string stepName,
        ProvisioningStepStatus status,
        DateTimeOffset now,
        string? detail = null,
        string? errorCode = null,
        Guid? completedBy = null)
    {
        EnsureUnfinished();

        var definition = ProvisioningPipeline.Require(Kind, stepName);

        var existing = Find(definition.Name);
        if (existing is not null)
        {
            existing.Update(status, detail, errorCode, completedBy, now);
            Settle(now);
            return null;
        }

        var created = ProvisioningStepResult.Create(
            Guid.CreateVersion7(),
            Id,
            _steps.Count + 1,
            definition.Name,
            definition.Mode,
            status,
            detail,
            errorCode,
            completedBy,
            now);

        _steps.Add(created);
        Settle(now);
        return created;
    }

    /// <summary>
    /// Completes a manual step on an operator's word.
    ///
    /// Deliberately refused for an automated step: an operator ticking off
    /// "health check" would be asserting a fact KNIGHT is perfectly able to
    /// check, and a store that never passed one must never reach Active
    /// (docs/store-provisioning.md §4).
    /// </summary>
    public ProvisioningStepResult? CompleteManualStep(string stepName, Guid completedBy, string? detail, DateTimeOffset now)
    {
        var definition = ProvisioningPipeline.Require(Kind, stepName);

        if (definition.Mode is not ProvisioningStepMode.Manual)
        {
            throw DomainException.Conflict(
                $"Step '{definition.Name}' is carried out by KNIGHT and cannot be ticked off by hand.");
        }

        // A step completed with no note keeps a note anyway, because the
        // alternative is a row that reads "done" beside the sentence explaining
        // what it was still waiting for — which is how a record starts lying.
        return ReportStep(
            definition.Name,
            ProvisioningStepStatus.Succeeded,
            now,
            string.IsNullOrWhiteSpace(detail) ? "Recorded by an operator." : detail,
            completedBy: completedBy);
    }

    public void Fail(
        string failureCode,
        string failureMessage,
        DateTimeOffset now,
        ProvisioningFailureClass failureClass = ProvisioningFailureClass.Transient)
    {
        EnsureUnfinished();

        State = ProvisioningState.Failed;
        FailureCode = RequireText(failureCode, "failure code", 100);
        FailureMessage = RequireText(failureMessage, "failure message", 2000);
        FailureClass = failureClass;
        CompletedAt = now;
        MarkUpdated(now);
    }

    /// <summary>
    /// Puts a failed job back on its failed step.
    ///
    /// Retrying resumes rather than restarts: the steps that succeeded stay
    /// succeeded, and only the one that failed is cleared. Re-running a
    /// succeeded credential step would issue a second secret nobody asked for.
    /// </summary>
    public void Retry(DateTimeOffset now)
    {
        if (State is not ProvisioningState.Failed)
        {
            throw DomainException.Conflict($"A job in state '{State}' is not failed and cannot be retried.");
        }

        foreach (var step in _steps.Where(step => step.Status is ProvisioningStepStatus.Failed))
        {
            step.Update(ProvisioningStepStatus.Pending, null, null, null, now);
        }

        State = ProvisioningState.Running;
        FailureCode = null;
        FailureMessage = null;
        FailureClass = null;
        AttemptCount += 1;
        CompletedAt = null;
        MarkUpdated(now);
    }

    public void Cancel(string reason, DateTimeOffset now)
    {
        EnsureUnfinished();

        State = ProvisioningState.Cancelled;
        FailureCode = "provisioning.cancelled";
        FailureMessage = RequireText(reason, "cancellation reason", 2000);
        CompletedAt = now;
        MarkUpdated(now);
    }

    public bool IsFinished => State is ProvisioningState.Succeeded or ProvisioningState.Failed or ProvisioningState.Cancelled;

    /// <summary>
    /// True when the job is sitting on a step only a person can finish. The
    /// dashboard shows these differently from a job that is merely slow: nothing
    /// will ever move without somebody doing something.
    /// </summary>
    public bool IsAwaitingOperator => State is ProvisioningState.AwaitingOperator;

    /// <summary>
    /// Derives the job's state from its steps.
    ///
    /// The state is never set from outside except to fail, cancel or retry — a
    /// job that says "running" while every step has succeeded is exactly the
    /// kind of disagreement between a status column and the facts that this
    /// aggregate exists to prevent.
    /// </summary>
    private void Settle(DateTimeOffset now)
    {
        var next = NextStep();

        if (next is null)
        {
            State = ProvisioningState.Succeeded;
            CompletedAt = now;
            MarkUpdated(now);
            return;
        }

        var definition = ProvisioningPipeline.Require(Kind, next);
        var recorded = Find(next);

        // A failed step wins over the step's mode. A manual step that was
        // attempted and failed — an export that could not be produced — is a
        // failure somebody has to retry, not a run politely waiting for a person
        // who already tried.
        State = recorded?.Status is ProvisioningStepStatus.Failed
            ? ProvisioningState.Failed
            : definition.Mode is ProvisioningStepMode.Manual
                ? ProvisioningState.AwaitingOperator
                : ProvisioningState.Running;

        if (State is ProvisioningState.Failed)
        {
            FailureCode ??= recorded?.ErrorCode ?? "provisioning.step.failed";
            FailureMessage ??= recorded?.Detail ?? $"Step '{next}' failed.";
            // A step that failed on its own is retryable until something says
            // otherwise; the coordinator reclassifies when it knows more.
            FailureClass ??= ProvisioningFailureClass.Transient;
            CompletedAt = now;
        }

        MarkUpdated(now);
    }

    private ProvisioningStepResult? Find(string name) =>
        _steps.Find(step => string.Equals(step.Name, name, StringComparison.Ordinal));

    private void EnsureUnfinished()
    {
        if (State is ProvisioningState.Succeeded or ProvisioningState.Cancelled)
        {
            throw DomainException.Conflict($"A job in state '{State}' has already finished.");
        }
    }

    private static string RequireText(string value, string what, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw DomainException.Validation($"A {what} is required.");
        }

        var trimmed = value.Trim();
        return trimmed.Length <= maxLength ? trimmed : trimmed[..maxLength];
    }
}

public enum ProvisioningKind
{
    Provision = 0,
    Deprovision = 1,
}

/// <summary>How a provisioning failure should be treated (docs/self-service-saas-plan.md §8).</summary>
public enum ProvisioningFailureClass
{
    /// <summary>A temporary condition — a timeout, a not-ready dependency. Retrying may succeed.</summary>
    Transient = 0,

    /// <summary>A bad request or state. Retrying the same way will fail the same way; it needs a fix first.</summary>
    Permanent = 1,

    /// <summary>Nothing automated will move it forward; a person has to act.</summary>
    ManualIntervention = 2,
}

public enum ProvisioningState
{
    Running = 0,

    /// <summary>Sitting on a manual step. Nothing moves until a person says it did.</summary>
    AwaitingOperator = 1,
    Succeeded = 2,
    Failed = 3,
    Cancelled = 4,
}
