using Knight.Domain.Common;
using Knight.Domain.Exceptions;
using Knight.Domain.Versioning;

namespace FeatureDelivery.Domain;

/// <summary>
/// The technical fact that a Feature is deployed in a store, and what state that
/// deployment is in (docs/feature-delivery.md §2 and §6).
///
/// This is emphatically not an entitlement. An entitlement is a commercial fact
/// about a customer, lives in the Subscriptions module, and answers "may they
/// use it". This answers "is it there and does it work". The two are separate
/// rows with separate lifecycles precisely because their disagreements are the
/// interesting cases: entitled but failed is a customer paying for something
/// broken, and installed but no longer entitled is code that must stop serving
/// without its data being destroyed.
///
/// Every transition is enforced here rather than by whoever calls it. A caller
/// that could set the state directly is a caller that can mark a store
/// "Installed" while its migration is still running, and the state is what the
/// dashboard, the alerting and the next job all read.
/// </summary>
public sealed class FeatureInstallation : AuditableEntity, ICustomerOwned
{
    public Guid StoreId { get; private set; }

    public Guid CustomerId { get; private set; }

    public Guid FeatureId { get; private set; }

    /// <summary>Denormalised so that reads and log lines can name the Feature without a join.</summary>
    public string FeatureSlug { get; private set; }

    public InstallationState State { get; private set; }

    /// <summary>The version actually deployed, or null while nothing has ever succeeded here.</summary>
    public string? InstalledVersion { get; private set; }

    public Guid? InstalledVersionId { get; private set; }

    /// <summary>The version a job in flight is moving towards; null when no job is in flight.</summary>
    public string? TargetVersion { get; private set; }

    public Guid? TargetVersionId { get; private set; }

    /// <summary>
    /// The version to return to if the job in flight fails. Captured when an
    /// upgrade starts, because by the time it fails the installed version has
    /// already been overwritten in the store.
    /// </summary>
    public string? PreviousVersion { get; private set; }

    public Guid? CurrentJobId { get; private set; }

    public string? FailureCode { get; private set; }

    public string? FailureMessage { get; private set; }

    public RollbackOutcome RollbackOutcome { get; private set; }

    /// <summary>
    /// Why the Feature is entitled but not installed — an unresolvable
    /// dependency, an incompatible store, shared hosting. Recorded rather than
    /// left blank: "entitled, not installed, no reason given" is the state that
    /// generates support tickets (docs/feature-delivery.md §8).
    /// </summary>
    public string? BlockingReason { get; private set; }

    public DateTimeOffset? InstalledAt { get; private set; }

    public DateTimeOffset? DisabledAt { get; private set; }

    public DateTimeOffset? UninstalledAt { get; private set; }

    /// <summary>
    /// When the retained data may be purged, set from the manifest's retention
    /// window at uninstall. Until then a customer who renews loses nothing.
    /// </summary>
    public DateTimeOffset? DataRetainedUntil { get; private set; }

    /// <summary>What the Feature's own health check last reported.</summary>
    public FeatureHealth Health { get; private set; }

    public DateTimeOffset? LastHealthCheckAt { get; private set; }

    private FeatureInstallation()
    {
        FeatureSlug = string.Empty;
    }

    private FeatureInstallation(
        Guid id,
        DateTimeOffset createdAt,
        Guid storeId,
        Guid customerId,
        Guid featureId,
        string featureSlug)
        : base(id, createdAt)
    {
        if (storeId == Guid.Empty)
        {
            throw DomainException.Validation("An installation must belong to a store.");
        }

        if (customerId == Guid.Empty)
        {
            throw DomainException.Validation("An installation must belong to a customer.");
        }

        if (featureId == Guid.Empty)
        {
            throw DomainException.Validation("An installation must name a feature.");
        }

        StoreId = storeId;
        CustomerId = customerId;
        FeatureId = featureId;
        FeatureSlug = string.IsNullOrWhiteSpace(featureSlug)
            ? throw DomainException.Validation("An installation must name a feature slug.")
            : featureSlug.Trim().ToLowerInvariant();

        State = InstallationState.NotInstalled;
        RollbackOutcome = RollbackOutcome.NotAttempted;
        Health = FeatureHealth.Unknown;
    }

    /// <summary>
    /// Creates the row that records "this store does not have this Feature".
    ///
    /// The row exists before anything is installed on purpose. "Entitled but not
    /// installed, blocked because the store is two majors too old" is a fact that
    /// needs somewhere to live, and a missing row cannot carry a reason.
    /// </summary>
    public static FeatureInstallation Create(
        Guid id,
        DateTimeOffset createdAt,
        Guid storeId,
        Guid customerId,
        Guid featureId,
        string featureSlug)
        => new(id, createdAt, storeId, customerId, featureId, featureSlug);

    // --- Transitions -------------------------------------------------------
    //
    // The state machine of docs/feature-delivery.md §6, in one place:
    //
    //   NotInstalled --queue--> Pending --claim--> Installing --> Installed
    //   Installed --upgrade--> Updating --> Installed | RollingBack --> Installed
    //   Installed --disable--> Disabled --enable--> Installed
    //   Installed | Disabled --uninstall--> Uninstalling --> NotInstalled
    //   any in-flight state --fail--> Failed --retry--> Pending
    //
    // Everything not drawn there is rejected below.

    /// <summary>Accepts a queued install or upgrade job and records what it is aiming at.</summary>
    public void QueueJob(Guid jobId, Guid targetVersionId, string targetVersion, DateTimeOffset now)
    {
        if (State is not (InstallationState.NotInstalled or InstallationState.Installed or
            InstallationState.Disabled or InstallationState.Failed))
        {
            throw DomainException.Conflict(
                $"A job cannot be queued for an installation in state '{State}'; one is already in flight.");
        }

        _ = SemanticVersion.Parse(targetVersion);

        // The version to roll back to is captured now, not at failure time: by
        // the time an upgrade fails, the store has already been changed.
        PreviousVersion = InstalledVersion;

        CurrentJobId = jobId;
        TargetVersionId = targetVersionId;
        TargetVersion = targetVersion;
        State = InstallationState.Pending;
        ClearFailure();
        MarkUpdated(now);
    }

    /// <summary>The agent has claimed the job and work has started in the store.</summary>
    public void BeginWork(Guid jobId, DateTimeOffset now)
    {
        RequireCurrentJob(jobId);

        if (State is not InstallationState.Pending)
        {
            throw DomainException.Conflict($"Work cannot begin on an installation in state '{State}'.");
        }

        // An upgrade and a first install run the same pipeline but are different
        // states, because a failure during an upgrade has a working version to
        // return to and a first install does not.
        State = InstalledVersion is null ? InstallationState.Installing : InstallationState.Updating;
        MarkUpdated(now);
    }

    /// <summary>The pipeline finished: the Feature is deployed, migrated, configured, enabled and healthy.</summary>
    public void MarkInstalled(Guid jobId, DateTimeOffset now)
    {
        RequireCurrentJob(jobId);

        if (State is not (InstallationState.Installing or InstallationState.Updating or InstallationState.RollingBack))
        {
            throw DomainException.Conflict($"An installation in state '{State}' cannot be marked installed.");
        }

        // A completed rollback lands on the version it restored, not on the one
        // the failed job was aiming at.
        if (State is InstallationState.RollingBack)
        {
            InstalledVersion = PreviousVersion;
            InstalledVersionId = null;
        }
        else
        {
            InstalledVersion = TargetVersion;
            InstalledVersionId = TargetVersionId;
        }

        State = InstallationState.Installed;
        InstalledAt = now;
        DisabledAt = null;
        UninstalledAt = null;
        DataRetainedUntil = null;
        TargetVersion = null;
        TargetVersionId = null;
        CurrentJobId = null;
        ClearFailure();
        MarkUpdated(now);
    }

    /// <summary>A step failed and no rollback was possible or needed.</summary>
    public void MarkFailed(Guid jobId, string failureCode, string failureMessage, RollbackOutcome rollbackOutcome, DateTimeOffset now)
    {
        RequireCurrentJob(jobId);

        if (State is not (InstallationState.Pending or InstallationState.Installing or
            InstallationState.Updating or InstallationState.RollingBack or InstallationState.Uninstalling))
        {
            throw DomainException.Conflict($"An installation in state '{State}' cannot fail; nothing is in flight.");
        }

        State = InstallationState.Failed;
        FailureCode = Require(failureCode, "failure code", 100);
        FailureMessage = Require(failureMessage, "failure message", 2000);
        RollbackOutcome = rollbackOutcome;
        TargetVersion = null;
        TargetVersionId = null;
        CurrentJobId = null;
        Health = FeatureHealth.Unknown;
        MarkUpdated(now);
    }

    /// <summary>A step failed and the pipeline is walking back what it already did.</summary>
    public void BeginRollback(Guid jobId, DateTimeOffset now)
    {
        RequireCurrentJob(jobId);

        if (State is not (InstallationState.Installing or InstallationState.Updating))
        {
            throw DomainException.Conflict($"An installation in state '{State}' has nothing to roll back.");
        }

        State = InstallationState.RollingBack;
        MarkUpdated(now);
    }

    /// <summary>
    /// The rollback could not finish on its own — an irreversible migration had
    /// already applied. KNIGHT does not guess past this point
    /// (docs/adr/0016-feature-migration-and-removal-policy.md).
    /// </summary>
    public void RequireManualIntervention(Guid jobId, string failureCode, string failureMessage, DateTimeOffset now)
        => MarkFailed(jobId, failureCode, failureMessage, RollbackOutcome.ManualInterventionRequired, now);

    /// <summary>
    /// Switches the Feature off while leaving its code and data in place. This is
    /// what losing an entitlement does — never an uninstall — so that a customer
    /// who renews next week finds their data where they left it
    /// (docs/feature-delivery.md §11).
    /// </summary>
    public void Disable(DateTimeOffset now)
    {
        if (State is InstallationState.Disabled)
        {
            return;
        }

        // `Failed` with a version on the store is a Feature that is running:
        // an upgrade that failed at `verify` never touched the working install,
        // and the row stays Failed until somebody looks at it. Refusing to
        // disable it meant the store ran the Disable job, reported it, and had
        // the report rejected — so the job sat in `Running` for ever and the
        // Feature went on serving for a customer who had stopped paying.
        //
        // The same shape as the rollback defect phase 18 found, on the path a
        // subscription takes when it ends at midnight. Found by the delivery
        // drill in phase 20.
        var serving = State is InstallationState.Installed
            || (State is InstallationState.Failed && InstalledVersion is not null);

        if (!serving)
        {
            throw DomainException.Conflict($"An installation in state '{State}' cannot be disabled.");
        }

        State = InstallationState.Disabled;
        DisabledAt = now;
        MarkUpdated(now);
    }

    public void Enable(DateTimeOffset now)
    {
        if (State is InstallationState.Installed)
        {
            return;
        }

        if (State is not InstallationState.Disabled)
        {
            throw DomainException.Conflict($"An installation in state '{State}' cannot be enabled.");
        }

        State = InstallationState.Installed;
        DisabledAt = null;
        MarkUpdated(now);
    }

    public void BeginUninstall(Guid jobId, DateTimeOffset now)
    {
        if (State is not (InstallationState.Installed or InstallationState.Disabled or InstallationState.Failed))
        {
            throw DomainException.Conflict($"An installation in state '{State}' cannot be uninstalled.");
        }

        CurrentJobId = jobId;
        State = InstallationState.Uninstalling;
        MarkUpdated(now);
    }

    /// <summary>
    /// The code is gone. The data is not: it is kept for the manifest's retention
    /// window, and only the purge that follows actually deletes it.
    /// </summary>
    public void MarkUninstalled(Guid jobId, int dataRetentionDays, DateTimeOffset now)
    {
        RequireCurrentJob(jobId);

        if (State is not InstallationState.Uninstalling)
        {
            throw DomainException.Conflict($"An installation in state '{State}' cannot be marked uninstalled.");
        }

        if (dataRetentionDays < 0)
        {
            throw DomainException.Validation("A retention window cannot be negative.");
        }

        State = InstallationState.NotInstalled;
        InstalledVersion = null;
        InstalledVersionId = null;
        PreviousVersion = null;
        TargetVersion = null;
        TargetVersionId = null;
        CurrentJobId = null;
        InstalledAt = null;
        DisabledAt = null;
        UninstalledAt = now;
        DataRetainedUntil = dataRetentionDays == 0 ? now : now.AddDays(dataRetentionDays);
        Health = FeatureHealth.Unknown;
        ClearFailure();
        MarkUpdated(now);
    }

    /// <summary>The retained data has been deleted; nothing of this Feature is left in the store.</summary>
    public void MarkPurged(DateTimeOffset now)
    {
        if (State is not InstallationState.NotInstalled || UninstalledAt is null)
        {
            throw DomainException.Conflict("Only the data of an uninstalled feature can be purged.");
        }

        DataRetainedUntil = null;
        MarkUpdated(now);
    }

    /// <summary>
    /// Records why an entitled Feature is not installed. Setting a reason does not
    /// change the state: the store genuinely does not have the Feature, and
    /// inventing a state for "wanted but impossible" would put it outside the
    /// machine that everything else reads.
    /// </summary>
    public void RecordBlockingReason(string? reason, DateTimeOffset now)
    {
        BlockingReason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim();
        MarkUpdated(now);
    }

    /// <summary>Records what the Feature's own health check said.</summary>
    public void RecordHealth(FeatureHealth health, DateTimeOffset now)
    {
        Health = health;
        LastHealthCheckAt = now;
        MarkUpdated(now);
    }

    /// <summary>True when a new job may be queued: nothing is in flight.</summary>
    public bool CanAcceptJob => State is InstallationState.NotInstalled or InstallationState.Installed
        or InstallationState.Disabled or InstallationState.Failed;

    /// <summary>True when the store should be serving this Feature's code right now.</summary>
    public bool IsServing => State is InstallationState.Installed;

    private void RequireCurrentJob(Guid jobId)
    {
        if (jobId == Guid.Empty)
        {
            throw DomainException.Validation("A transition must name the job that caused it.");
        }

        // A report from a job that is no longer the current one is a late reply
        // from a job that already timed out. Applying it would let a stale agent
        // overwrite the state of the job that replaced it.
        if (CurrentJobId is not null && CurrentJobId != jobId)
        {
            throw DomainException.Conflict(
                "This report belongs to a job that is no longer the installation's current job.");
        }
    }

    private void ClearFailure()
    {
        FailureCode = null;
        FailureMessage = null;
        RollbackOutcome = RollbackOutcome.NotAttempted;
        BlockingReason = null;
    }

    private static string Require(string value, string what, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw DomainException.Validation($"A {what} is required.");
        }

        var trimmed = value.Trim();
        return trimmed.Length <= maxLength ? trimmed : trimmed[..maxLength];
    }
}

/// <summary>The states of docs/feature-delivery.md §6.</summary>
public enum InstallationState
{
    NotInstalled = 0,
    Pending = 1,
    Installing = 2,
    Installed = 3,
    Updating = 4,
    RollingBack = 5,
    Disabled = 6,
    Uninstalling = 7,
    Failed = 8,
}

/// <summary>
/// What happened when a failed job tried to undo itself. This is the field an
/// operator reads first after an install fails, because it is the difference
/// between "nothing to do" and "a database needs a human tonight".
/// </summary>
public enum RollbackOutcome
{
    /// <summary>No rollback was needed, or the job never got far enough to need one.</summary>
    NotAttempted = 0,

    /// <summary>Everything that was applied has been undone; the store is as it was.</summary>
    RolledBack = 1,

    /// <summary>The package and configuration were restored, but some database change was not.</summary>
    PartiallyRolledBack = 2,

    /// <summary>An irreversible migration had already applied. KNIGHT stopped rather than guessing.</summary>
    ManualInterventionRequired = 3,
}

/// <summary>What the Feature's own health check reported the last time it ran.</summary>
public enum FeatureHealth
{
    Unknown = 0,
    Healthy = 1,
    Degraded = 2,
    Unhealthy = 3,
}
