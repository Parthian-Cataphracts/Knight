using Knight.Domain.Common;
using Knight.Domain.Exceptions;

namespace FeatureDelivery.Domain;

/// <summary>
/// One unit of work an agent carries out inside a store: install this version,
/// upgrade to that one, apply this configuration, uninstall this Feature
/// (docs/feature-delivery.md §7).
///
/// The job is the only thing an agent is ever told to do. There is no "run this
/// command" job type and there never will be — the agent exposes the named
/// operations of this pipeline and nothing else, so a compromised control plane
/// cannot turn into arbitrary code execution on every store at once
/// (docs/adr/0015-feature-delivery-mechanism.md).
///
/// Steps are recorded individually rather than as a status field, because "it
/// failed" is not an answer anybody can act on. Which step, what it returned, how
/// long it took and whether the one before it had already changed the database
/// are the difference between a retry and an incident.
/// </summary>
public sealed class FeatureInstallationJob : AuditableEntity, ICustomerOwned
{
    public Guid StoreId { get; private set; }

    public Guid CustomerId { get; private set; }

    public Guid InstallationId { get; private set; }

    public Guid FeatureId { get; private set; }

    public string FeatureSlug { get; private set; }

    public JobType Type { get; private set; }

    public JobState State { get; private set; }

    /// <summary>The version this job is moving the store to; null for a plain uninstall.</summary>
    public string? TargetVersion { get; private set; }

    public Guid? TargetVersionId { get; private set; }

    /// <summary>
    /// The caller's key for this request. Two requests carrying the same key are
    /// the same request — a retried HTTP call, a redelivered entitlement event —
    /// and must produce one job, not two racing installs of the same Feature.
    /// </summary>
    public string IdempotencyKey { get; private set; }

    /// <summary>Ties every step, log line and audit entry of this job to the request that started it.</summary>
    public string CorrelationId { get; private set; }

    /// <summary>
    /// The W3C <c>traceparent</c> of the request that queued this job, handed to
    /// the agent so its own spans join that trace instead of starting an
    /// unrelated one (docs/observability.md §4).
    ///
    /// Stored rather than recomputed because the agent runs minutes or hours
    /// later, in a different process, long after the originating request has
    /// finished — the trace it belongs to is a historical fact about why the job
    /// exists, not something the claim can derive.
    /// </summary>
    public string? TraceParent { get; private set; }

    public DateTimeOffset QueuedAt { get; private set; }

    public DateTimeOffset? ClaimedAt { get; private set; }

    /// <summary>
    /// When an unfinished claim is considered abandoned. An agent that dies
    /// mid-install leaves a job nobody will ever report on, and without a
    /// deadline that job blocks the store's queue forever.
    /// </summary>
    public DateTimeOffset? ClaimExpiresAt { get; private set; }

    public DateTimeOffset? CompletedAt { get; private set; }

    public int AttemptCount { get; private set; }

    public int MaxAttempts { get; private set; }

    public string? FailureCode { get; private set; }

    public string? FailureMessage { get; private set; }

    public RollbackOutcome RollbackOutcome { get; private set; }

    public Guid RequestedBy { get; private set; }

    /// <summary>How the job came to exist — an operator asked, or an entitlement changed.</summary>
    public JobTrigger Trigger { get; private set; }

    private readonly List<JobStepResult> _steps = [];

    public IReadOnlyCollection<JobStepResult> Steps => _steps.AsReadOnly();

    /// <summary>How far along the job is, for the dashboard's progress bar.</summary>
    public int CompletedStepCount => _steps.Count(step => step.Status is StepStatus.Succeeded or StepStatus.Skipped);

    public int TotalStepCount { get; private set; }

    /// <summary>
    /// Whether this job delivers code into the store or a configuration to it.
    ///
    /// It decides which steps the job has, so it is on the job rather than
    /// looked up from the version each time something asks: a job that was
    /// queued under one pipeline must keep the step list it was queued with,
    /// even if the Feature is republished under the other architecture while it
    /// is in flight (adr/0033).
    ///
    /// Defaulted to in-process, so every job row written before this existed
    /// still reads correctly.
    /// </summary>
    public DeliveryArchitecture Architecture { get; private set; }

    private FeatureInstallationJob()
    {
        FeatureSlug = string.Empty;
        IdempotencyKey = string.Empty;
        CorrelationId = string.Empty;
    }

    private FeatureInstallationJob(
        Guid id,
        DateTimeOffset createdAt,
        Guid storeId,
        Guid customerId,
        Guid installationId,
        Guid featureId,
        string featureSlug,
        JobType type,
        Guid? targetVersionId,
        string? targetVersion,
        string idempotencyKey,
        string correlationId,
        Guid requestedBy,
        JobTrigger trigger,
        int maxAttempts,
        int totalStepCount)
        : base(id, createdAt)
    {
        StoreId = storeId;
        CustomerId = customerId;
        InstallationId = installationId;
        FeatureId = featureId;
        FeatureSlug = featureSlug;
        Type = type;
        TargetVersionId = targetVersionId;
        TargetVersion = targetVersion;
        IdempotencyKey = idempotencyKey;
        CorrelationId = correlationId;
        RequestedBy = requestedBy;
        Trigger = trigger;
        MaxAttempts = maxAttempts;
        TotalStepCount = totalStepCount;
        Architecture = DeliveryArchitecture.InProcess;
        QueuedAt = createdAt;
        State = JobState.Queued;
        RollbackOutcome = RollbackOutcome.NotAttempted;
    }

    public static FeatureInstallationJob Queue(
        Guid id,
        DateTimeOffset now,
        Guid storeId,
        Guid customerId,
        Guid installationId,
        Guid featureId,
        string featureSlug,
        JobType type,
        Guid? targetVersionId,
        string? targetVersion,
        string idempotencyKey,
        string correlationId,
        Guid requestedBy,
        JobTrigger trigger,
        int maxAttempts = 3,
        string? traceParent = null,
        DeliveryArchitecture architecture = DeliveryArchitecture.InProcess)
    {
        if (type is not JobType.Uninstall && string.IsNullOrWhiteSpace(targetVersion))
        {
            throw DomainException.Validation($"A {type} job must name the version it is moving the store to.");
        }

        if (maxAttempts is < 1 or > 10)
        {
            throw DomainException.Validation("A job must allow between one and ten attempts.");
        }

        return new FeatureInstallationJob(
            id,
            now,
            storeId,
            customerId,
            installationId,
            featureId,
            RequireText(featureSlug, "feature slug", 100).ToLowerInvariant(),
            type,
            targetVersionId,
            string.IsNullOrWhiteSpace(targetVersion) ? null : targetVersion.Trim(),
            RequireText(idempotencyKey, "idempotency key", 200),
            RequireText(correlationId, "correlation id", 100),
            requestedBy,
            trigger,
            maxAttempts,
            JobPipeline.StepsFor(type, architecture).Count)
        {
            Architecture = architecture,
            // Length-capped: a traceparent is a fixed 55-character header, and
            // anything longer did not come from a tracing library.
            TraceParent = string.IsNullOrWhiteSpace(traceParent) || traceParent.Length > 64
                ? null
                : traceParent.Trim(),
        };
    }

    /// <summary>
    /// An agent takes the job. The claim carries a deadline so that an agent
    /// which dies mid-install does not hold the store's queue forever.
    /// </summary>
    public void Claim(DateTimeOffset now, TimeSpan claimTimeout)
    {
        if (State is not JobState.Queued)
        {
            throw DomainException.Conflict($"A job in state '{State}' cannot be claimed.");
        }

        State = JobState.Running;
        ClaimedAt = now;
        ClaimExpiresAt = now.Add(claimTimeout);
        AttemptCount++;
        MarkUpdated(now);
    }

    /// <summary>
    /// Records the outcome of one step.
    ///
    /// Reporting the same step twice is not an error: an agent that succeeded at
    /// step five and lost the network before the reply landed will report step
    /// five again, and refusing it would fail a job that actually worked. The
    /// later report replaces the earlier one for that step rather than appending,
    /// so the record stays one row per step (docs/feature-delivery.md §7).
    /// </summary>
    /// <returns>
    /// The step row if this report created one, or null when it updated a step
    /// that had already been reported. The caller needs to know, because a newly
    /// created child of a loaded aggregate has to be registered with the
    /// persistence layer explicitly — the same reason the plan and subscription
    /// repositories expose RegisterNewFeature.
    /// </returns>
    public JobStepResult? ReportStep(
        string stepName,
        StepStatus status,
        DateTimeOffset now,
        string? output = null,
        string? errorCode = null,
        int? durationMilliseconds = null)
    {
        if (State is not JobState.Running)
        {
            throw DomainException.Conflict($"A job in state '{State}' is not running and cannot report progress.");
        }

        var name = RequireText(stepName, "step name", 100);

        if (!JobPipeline.StepsFor(Type, Architecture).Contains(name, StringComparer.Ordinal))
        {
            throw DomainException.Validation($"'{name}' is not a step of a {Type} job.");
        }

        JobStepResult? created = null;

        var existing = _steps.Find(step => string.Equals(step.Name, name, StringComparison.Ordinal));
        if (existing is not null)
        {
            existing.Update(status, output, errorCode, durationMilliseconds, now);
        }
        else
        {
            created = JobStepResult.Create(
                Guid.CreateVersion7(),
                Id,
                _steps.Count + 1,
                name,
                status,
                output,
                errorCode,
                durationMilliseconds,
                now);

            _steps.Add(created);
        }

        // A claim is extended by evidence of life rather than by a heartbeat of
        // its own: a step report is proof the agent is still working.
        if (ClaimExpiresAt is not null && now > ClaimedAt)
        {
            ClaimExpiresAt = now.Add(ClaimExpiresAt.Value - ClaimedAt!.Value);
        }

        MarkUpdated(now);
        return created;
    }

    public void Succeed(DateTimeOffset now)
    {
        if (State is not JobState.Running)
        {
            throw DomainException.Conflict($"A job in state '{State}' cannot succeed.");
        }

        State = JobState.Succeeded;
        CompletedAt = now;
        ClaimExpiresAt = null;
        MarkUpdated(now);
    }

    public void Fail(string failureCode, string failureMessage, RollbackOutcome rollbackOutcome, DateTimeOffset now)
    {
        if (State is not (JobState.Running or JobState.Queued))
        {
            throw DomainException.Conflict($"A job in state '{State}' cannot fail.");
        }

        State = JobState.Failed;
        FailureCode = RequireText(failureCode, "failure code", 100);
        FailureMessage = RequireText(failureMessage, "failure message", 2000);
        RollbackOutcome = rollbackOutcome;
        CompletedAt = now;
        ClaimExpiresAt = null;
        MarkUpdated(now);
    }

    /// <summary>
    /// Returns an abandoned job to the queue, or fails it for good once it has
    /// used up its attempts.
    ///
    /// A timed-out job is never simply retried forever. An install that hangs
    /// three times is not going to work on the fourth, and a queue that keeps
    /// retrying it is a queue where the store's real work never runs.
    /// </summary>
    public bool TimeOut(DateTimeOffset now)
    {
        if (State is not JobState.Running)
        {
            throw DomainException.Conflict($"A job in state '{State}' cannot time out.");
        }

        if (AttemptCount >= MaxAttempts)
        {
            Fail(
                "job.timeout",
                $"The agent stopped reporting and the job exhausted its {MaxAttempts} attempts.",
                RollbackOutcome.NotAttempted,
                now);
            return false;
        }

        State = JobState.Queued;
        ClaimedAt = null;
        ClaimExpiresAt = null;
        MarkUpdated(now);
        return true;
    }

    public void Cancel(string reason, DateTimeOffset now)
    {
        if (State is JobState.Succeeded or JobState.Failed or JobState.Cancelled)
        {
            throw DomainException.Conflict($"A job in state '{State}' has already finished.");
        }

        State = JobState.Cancelled;
        FailureCode = "job.cancelled";
        FailureMessage = RequireText(reason, "cancellation reason", 2000);
        CompletedAt = now;
        ClaimExpiresAt = null;
        MarkUpdated(now);
    }

    /// <summary>True when the claim has lapsed and the job needs returning to the queue.</summary>
    public bool IsClaimExpired(DateTimeOffset now) =>
        State is JobState.Running && ClaimExpiresAt is not null && now >= ClaimExpiresAt;

    /// <summary>True once the job has reached a state it will never leave.</summary>
    public bool IsFinished => State is JobState.Succeeded or JobState.Failed or JobState.Cancelled;

    /// <summary>
    /// The step the pipeline should carry out next, or null when every step is
    /// done. This is what makes a job idempotent: an agent that retries asks what
    /// remains rather than starting from the top.
    /// </summary>
    public string? NextStep()
    {
        foreach (var step in JobPipeline.StepsFor(Type, Architecture))
        {
            var recorded = _steps.Find(item => string.Equals(item.Name, step, StringComparison.Ordinal));

            // A skipped step is finished, not pending. A manifest with no
            // migrations skips the migrate step, and treating that as unfinished
            // would leave the job asking for it forever.
            if (recorded is null || recorded.Status is not (StepStatus.Succeeded or StepStatus.Skipped))
            {
                return step;
            }
        }

        return null;
    }

    /// <summary>
    /// The steps that succeeded, newest first — the order a rollback has to undo
    /// them in.
    /// </summary>
    public IReadOnlyList<JobStepResult> SucceededStepsInReverse() =>
        [.. _steps.Where(step => step.Status is StepStatus.Succeeded).OrderByDescending(step => step.Sequence)];

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

/// <summary>
/// The named operations an agent will carry out. The list is closed: an agent
/// that receives a type it does not recognise refuses the job rather than
/// improvising.
/// </summary>
public enum JobType
{
    Install = 0,
    Upgrade = 1,
    ApplyConfiguration = 2,
    Enable = 3,
    Disable = 4,
    Uninstall = 5,
    Rollback = 6,
}

public enum JobState
{
    Queued = 0,
    Running = 1,
    Succeeded = 2,
    Failed = 3,
    Cancelled = 4,
}

/// <summary>Why the job exists. An automatic job and one an operator asked for are read very differently after an incident.</summary>
public enum JobTrigger
{
    /// <summary>An operator or customer asked for it in the dashboard.</summary>
    Manual = 0,

    /// <summary>An entitlement was granted or revoked.</summary>
    Entitlement = 1,

    /// <summary>A store was provisioned and needs its plan's Features.</summary>
    Provisioning = 2,

    /// <summary>Reconciliation found the store disagreeing with what KNIGHT believes.</summary>
    Reconciliation = 3,
}

/// <summary>
/// The steps of each job type, in order.
///
/// The pipeline is data rather than code so that KNIGHT and the store agree on
/// exactly which steps exist and what they are called. The agent reports these
/// names; anything else is refused, which is what stops a rogue or outdated agent
/// from inventing a step KNIGHT would then record as progress.
/// </summary>
public static class JobPipeline
{
    public const string Preflight = "preflight";
    public const string Fetch = "fetch";
    public const string Verify = "verify";
    public const string Backup = "backup";
    public const string Install = "install";

    /// <summary>
    /// Creates the database extensions the manifest declares, before any
    /// migration runs.
    ///
    /// Its own step rather than part of <see cref="Migrate"/> because it is the
    /// one part of the schema work that a store's database user is routinely not
    /// allowed to do — most managed PostgreSQL restricts it — and finding that
    /// out before a migration has applied is the difference between a job that
    /// failed and a job that has to be finished by hand
    /// (docs/adr/0031-database-extensions-are-declared-not-migrated.md).
    /// </summary>
    public const string CreateExtensions = "create-extensions";

    public const string Migrate = "migrate";
    public const string Configure = "configure";
    public const string Enable = "enable";
    public const string Reload = "reload";
    public const string HealthCheck = "healthcheck";
    public const string Disable = "disable";
    public const string RemovePackage = "remove-package";
    public const string RestorePackage = "restore-package";
    public const string ReverseMigrate = "reverse-migrate";

    private static readonly string[] InstallSteps =
        [Preflight, Fetch, Verify, Backup, Install, CreateExtensions, Migrate, Configure, Enable, Reload, HealthCheck];

    private static readonly string[] ConfigurationSteps = [Configure, Reload, HealthCheck];

    private static readonly string[] EnableSteps = [Enable, Reload, HealthCheck];

    private static readonly string[] DisableSteps = [Disable, Reload];

    private static readonly string[] UninstallSteps = [Disable, Backup, RemovePackage, Reload];

    /// <summary>
    /// Reverse the migrations **before** putting the old package back, not after.
    ///
    /// Django can only unapply a migration whose file it can still see. Restoring
    /// the previous package first removes the newer migration from disk, so the
    /// reverse that follows finds nothing to undo and silently leaves the schema
    /// where the upgrade left it — a rollback that moved the code back and not
    /// the database, which is the worst of both.
    ///
    /// Found by the delivery drill in phase 19, on the first rollback across two
    /// versions whose migrations actually differed. Phase 18's rollback used
    /// identical migrations and could not have seen it
    /// (docs/phase-19-verification.md).
    /// </summary>
    private static readonly string[] RollbackSteps = [ReverseMigrate, RestorePackage, Configure, Reload, HealthCheck];

    // --- The external-service pipelines ------------------------------------
    //
    // Deliberately built from the verbs that already exist rather than from new
    // ones. `install` means "make this Feature present in this store", and for a
    // service that is registering its webhooks and wiring its proxy routes -
    // the same relationship every runtime already has to the same verb, where
    // Django unpacks a Python distribution and node unpacks an npm package
    // (adr/0032 §3, adr/0033 §4).
    //
    // Not adding verbs is the whole reason this pivot does not break the three
    // agents. A store that meets an unknown step refuses the job, and phase 20
    // found the node store had been missing three of them for three phases
    // without anybody noticing. Every step below is one all three agents
    // already implement.

    /// <summary>
    /// No backup, no extensions, no migrate, no reload.
    ///
    /// Each absence is a fact about the architecture rather than a shortcut.
    /// There is no package to back up, no database to create an extension in,
    /// no schema to migrate, and nothing loaded into the store's process that a
    /// restart would replace.
    /// </summary>
    private static readonly string[] ExternalInstallSteps =
        [Preflight, Fetch, Verify, Configure, Install, Enable, HealthCheck];

    /// <summary>
    /// Restoring the previous configuration and re-applying it.
    ///
    /// The rollback that mattered for code - reverse the migrations before
    /// putting the old package back - has no counterpart here, because there is
    /// nothing in the store's database to reverse. That is the single largest
    /// operational difference between the two architectures.
    /// </summary>
    private static readonly string[] ExternalRollbackSteps =
        [RestorePackage, Configure, Install, Enable, HealthCheck];

    private static readonly string[] ExternalUninstallSteps = [Disable, RemovePackage];

    private static readonly string[] ExternalConfigurationSteps = [Configure, Install, HealthCheck];

    private static readonly string[] ExternalEnableSteps = [Enable, HealthCheck];

    private static readonly string[] ExternalDisableSteps = [Disable];

    /// <summary>
    /// The steps for a job, which depend on what is being delivered as well as
    /// on what is being done.
    /// </summary>
    public static IReadOnlyList<string> StepsFor(JobType type, DeliveryArchitecture architecture = DeliveryArchitecture.InProcess) =>
        architecture is DeliveryArchitecture.ExternalService
            ? ExternalStepsFor(type)
            : InProcessStepsFor(type);

    private static IReadOnlyList<string> InProcessStepsFor(JobType type) => type switch
    {
        JobType.Install or JobType.Upgrade => InstallSteps,
        JobType.ApplyConfiguration => ConfigurationSteps,
        JobType.Enable => EnableSteps,
        JobType.Disable => DisableSteps,
        JobType.Uninstall => UninstallSteps,
        JobType.Rollback => RollbackSteps,
        _ => throw DomainException.Validation($"'{type}' is not a known job type."),
    };

    private static IReadOnlyList<string> ExternalStepsFor(JobType type) => type switch
    {
        JobType.Install or JobType.Upgrade => ExternalInstallSteps,
        JobType.ApplyConfiguration => ExternalConfigurationSteps,
        JobType.Enable => ExternalEnableSteps,
        JobType.Disable => ExternalDisableSteps,
        JobType.Uninstall => ExternalUninstallSteps,
        JobType.Rollback => ExternalRollbackSteps,
        _ => throw DomainException.Validation($"'{type}' is not a known job type."),
    };

    /// <summary>
    /// Whether a step changes the store's database. Only these need a reverse
    /// migration to undo, and only these can make a rollback impossible.
    ///
    /// <see cref="CreateExtensions"/> writes to the database and is deliberately
    /// not here: an extension is shared state the Feature does not own, nothing
    /// ever drops it, and so it can neither need a reverse nor block one
    /// (docs/adr/0031-database-extensions-are-declared-not-migrated.md).
    /// </summary>
    public static bool TouchesDatabase(string step) =>
        string.Equals(step, Migrate, StringComparison.Ordinal) ||
        string.Equals(step, ReverseMigrate, StringComparison.Ordinal);
}

/// <summary>
/// What a job is delivering: code the store runs, or configuration it acts on.
///
/// The delivery module's own copy of the registry's <c>FeatureArchitecture</c>.
/// Duplicated rather than shared because modules do not reference their
/// siblings — the same reason <c>StoreCompatibilityContext</c> reduces hosting
/// to a boolean rather than importing the Stores module.
/// </summary>
public enum DeliveryArchitecture
{
    InProcess = 0,
    ExternalService = 1,
}
