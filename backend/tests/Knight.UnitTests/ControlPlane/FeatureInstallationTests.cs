using FeatureDelivery.Domain;
using Knight.Domain.Exceptions;
using Xunit;

namespace Knight.UnitTests.ControlPlane;

/// <summary>
/// The installation state machine of docs/feature-delivery.md §6, exercised
/// exhaustively — every legal transition once, and every illegal one refused.
///
/// The exhaustive part matters more than it looks. The state is what the
/// dashboard shows, what alerting fires on, and what decides whether the next job
/// may be queued; a transition nobody drew but the code allows is a store that
/// reports "Installed" while its migration is still running.
/// </summary>
public sealed class FeatureInstallationTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 19, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid StoreId = Guid.CreateVersion7();
    private static readonly Guid CustomerId = Guid.CreateVersion7();
    private static readonly Guid FeatureId = Guid.CreateVersion7();
    private static readonly Guid VersionId = Guid.CreateVersion7();

    private static FeatureInstallation New() =>
        FeatureInstallation.Create(Guid.CreateVersion7(), Now, StoreId, CustomerId, FeatureId, "analytics-core");

    /// <summary>Drives an installation to <see cref="InstallationState.Installed"/> the way a real job does.</summary>
    private static (FeatureInstallation Installation, Guid JobId) Installed(string version = "1.0.0")
    {
        var installation = New();
        var jobId = Guid.CreateVersion7();

        installation.QueueJob(jobId, VersionId, version, Now);
        installation.BeginWork(jobId, Now);
        installation.MarkInstalled(jobId, Now);

        return (installation, jobId);
    }

    // --- An operator-requested rollback ------------------------------------

    [Fact]
    public void AnOperatorRollback_LandsOnTheVersionItRestored()
    {
        // Phase 18 rolled a real store back and watched KNIGHT go on reporting
        // the version the store had just left. The job was queued beside an
        // installation that knew nothing about it, so the store's completion
        // report was refused with "an installation in state 'Installed' cannot
        // be marked installed" - the job stayed Running for ever and the control
        // plane's picture of the fleet was permanently wrong.
        var (installation, _) = Installed("1.0.0");

        var upgrade = Guid.CreateVersion7();
        installation.QueueJob(upgrade, VersionId, "1.0.1", Now);
        installation.BeginWork(upgrade, Now);
        installation.MarkInstalled(upgrade, Now);

        Assert.Equal("1.0.1", installation.InstalledVersion);
        Assert.Equal("1.0.0", installation.PreviousVersion);

        // What the rollback path now does: queue the job against the aggregate,
        // targeting the version being restored.
        var rollback = Guid.CreateVersion7();
        installation.QueueJob(rollback, VersionId, installation.PreviousVersion!, Now);
        installation.BeginWork(rollback, Now);
        installation.MarkInstalled(rollback, Now);

        Assert.Equal(InstallationState.Installed, installation.State);
        Assert.Equal("1.0.0", installation.InstalledVersion);
    }

    [Fact]
    public void AnInstallationInFlight_RefusesASecondJob()
    {
        // The guard the rollback path now goes through, stated so that queuing
        // one cannot quietly become a way around it.
        var (installation, _) = Installed("1.0.0");

        installation.QueueJob(Guid.CreateVersion7(), VersionId, "1.0.1", Now);

        Assert.Throws<DomainException>(() =>
            installation.QueueJob(Guid.CreateVersion7(), VersionId, "1.0.2", Now));
    }

    // --- Creation ----------------------------------------------------------

    [Fact]
    public void ANewInstallation_RecordsThatTheStoreDoesNotHaveTheFeature()
    {
        var installation = New();

        Assert.Equal(InstallationState.NotInstalled, installation.State);
        Assert.Null(installation.InstalledVersion);
        Assert.Equal(RollbackOutcome.NotAttempted, installation.RollbackOutcome);
        Assert.Equal(FeatureHealth.Unknown, installation.Health);
        Assert.True(installation.CanAcceptJob);
        Assert.False(installation.IsServing);
    }

    [Fact]
    public void TheSlugIsNormalised()
    {
        var installation = FeatureInstallation.Create(
            Guid.CreateVersion7(), Now, StoreId, CustomerId, FeatureId, "  Analytics-Core  ");

        Assert.Equal("analytics-core", installation.FeatureSlug);
    }

    [Theory]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    public void AnInstallationWithoutItsOwners_IsRefused(bool noStore, bool noCustomer, bool noFeature)
    {
        Assert.Throws<DomainException>(() => FeatureInstallation.Create(
            Guid.CreateVersion7(),
            Now,
            noStore ? Guid.Empty : StoreId,
            noCustomer ? Guid.Empty : CustomerId,
            noFeature ? Guid.Empty : FeatureId,
            "analytics-core"));
    }

    // --- The happy path ----------------------------------------------------

    [Fact]
    public void AFirstInstall_GoesPendingThenInstallingThenInstalled()
    {
        var installation = New();
        var jobId = Guid.CreateVersion7();

        installation.QueueJob(jobId, VersionId, "1.4.0", Now);
        Assert.Equal(InstallationState.Pending, installation.State);
        Assert.Equal("1.4.0", installation.TargetVersion);
        Assert.False(installation.CanAcceptJob);

        installation.BeginWork(jobId, Now);
        Assert.Equal(InstallationState.Installing, installation.State);

        installation.MarkInstalled(jobId, Now);
        Assert.Equal(InstallationState.Installed, installation.State);
        Assert.Equal("1.4.0", installation.InstalledVersion);
        Assert.Equal(VersionId, installation.InstalledVersionId);
        Assert.Null(installation.TargetVersion);
        Assert.Null(installation.CurrentJobId);
        Assert.True(installation.IsServing);
    }

    [Fact]
    public void AnUpgrade_IsUpdatingRatherThanInstalling()
    {
        // The distinction is not cosmetic: an upgrade has a working version to
        // return to and a first install does not.
        var (installation, _) = Installed("1.0.0");
        var jobId = Guid.CreateVersion7();

        installation.QueueJob(jobId, Guid.CreateVersion7(), "2.0.0", Now);
        installation.BeginWork(jobId, Now);

        Assert.Equal(InstallationState.Updating, installation.State);
        Assert.Equal("1.0.0", installation.PreviousVersion);
    }

    [Fact]
    public void ThePreviousVersion_IsCapturedWhenTheJobIsQueuedNotWhenItFails()
    {
        // By the time an upgrade fails, the store has already been changed.
        var (installation, _) = Installed("1.0.0");
        var jobId = Guid.CreateVersion7();

        installation.QueueJob(jobId, Guid.CreateVersion7(), "2.0.0", Now);

        Assert.Equal("1.0.0", installation.PreviousVersion);
    }

    [Fact]
    public void AMalformedTargetVersion_IsRefused()
    {
        var installation = New();

        Assert.Throws<DomainException>(() =>
            installation.QueueJob(Guid.CreateVersion7(), VersionId, "not-a-version", Now));
    }

    // --- Failure and rollback ----------------------------------------------

    [Fact]
    public void AFailedInstall_RecordsTheCodeMessageAndRollbackOutcome()
    {
        var installation = New();
        var jobId = Guid.CreateVersion7();

        installation.QueueJob(jobId, VersionId, "1.0.0", Now);
        installation.BeginWork(jobId, Now);
        installation.MarkFailed(jobId, "migrate.failed", "Migration 2 of 4 failed.", RollbackOutcome.RolledBack, Now);

        Assert.Equal(InstallationState.Failed, installation.State);
        Assert.Equal("migrate.failed", installation.FailureCode);
        Assert.Equal(RollbackOutcome.RolledBack, installation.RollbackOutcome);
        Assert.Null(installation.CurrentJobId);

        // Failed is not terminal: there has to be a way out.
        Assert.True(installation.CanAcceptJob);
    }

    [Fact]
    public void ARollbackThatSucceeds_RestoresThePreviousVersionNotTheTarget()
    {
        var (installation, _) = Installed("1.0.0");
        var jobId = Guid.CreateVersion7();

        installation.QueueJob(jobId, Guid.CreateVersion7(), "2.0.0", Now);
        installation.BeginWork(jobId, Now);
        installation.BeginRollback(jobId, Now);
        Assert.Equal(InstallationState.RollingBack, installation.State);

        installation.MarkInstalled(jobId, Now);

        Assert.Equal(InstallationState.Installed, installation.State);
        Assert.Equal("1.0.0", installation.InstalledVersion);
    }

    [Fact]
    public void AnIrreversibleMigration_StopsAtManualInterventionRequired()
    {
        // KNIGHT does not guess past an irreversible migration (ADR 0016).
        var (installation, _) = Installed("1.0.0");
        var jobId = Guid.CreateVersion7();

        installation.QueueJob(jobId, Guid.CreateVersion7(), "2.0.0", Now);
        installation.BeginWork(jobId, Now);
        installation.BeginRollback(jobId, Now);
        installation.RequireManualIntervention(
            jobId, "rollback.irreversible", "Migration 0003 is irreversible and has applied.", Now);

        Assert.Equal(InstallationState.Failed, installation.State);
        Assert.Equal(RollbackOutcome.ManualInterventionRequired, installation.RollbackOutcome);
        Assert.Contains("0003", installation.FailureMessage!, StringComparison.Ordinal);
    }

    [Fact]
    public void AFailedInstallation_CanBeRetried()
    {
        var installation = New();
        var first = Guid.CreateVersion7();

        installation.QueueJob(first, VersionId, "1.0.0", Now);
        installation.BeginWork(first, Now);
        installation.MarkFailed(first, "fetch.failed", "The artifact could not be fetched.", RollbackOutcome.NotAttempted, Now);

        var second = Guid.CreateVersion7();
        installation.QueueJob(second, VersionId, "1.0.0", Now);

        Assert.Equal(InstallationState.Pending, installation.State);

        // The retry starts clean rather than carrying the last failure forward.
        Assert.Null(installation.FailureCode);
        Assert.Equal(RollbackOutcome.NotAttempted, installation.RollbackOutcome);
    }

    [Fact]
    public void ARollbackCannotBeginWhenNothingIsInFlight()
    {
        var (installation, _) = Installed();

        Assert.Throws<DomainException>(() => installation.BeginRollback(Guid.CreateVersion7(), Now));
    }

    // --- Disable, enable, uninstall ----------------------------------------

    [Fact]
    public void LosingAnEntitlement_DisablesRatherThanUninstalls()
    {
        // The default policy of docs/feature-delivery.md §11: the code stays, the
        // data stays, the feature stops serving.
        var (installation, _) = Installed("1.4.0");

        installation.Disable(Now);

        Assert.Equal(InstallationState.Disabled, installation.State);
        Assert.Equal("1.4.0", installation.InstalledVersion);
        Assert.Null(installation.UninstalledAt);
        Assert.Null(installation.DataRetainedUntil);
        Assert.False(installation.IsServing);
    }

    [Fact]
    public void DisablingTwice_IsNotAnError()
    {
        // Entitlement reconciliation runs repeatedly and must be safe to re-run.
        var (installation, _) = Installed();

        installation.Disable(Now);
        installation.Disable(Now.AddMinutes(1));

        Assert.Equal(InstallationState.Disabled, installation.State);
    }

    [Fact]
    public void ARenewedCustomer_GetsTheFeatureBackWithItsData()
    {
        var (installation, _) = Installed("1.4.0");
        installation.Disable(Now);

        installation.Enable(Now.AddDays(7));

        Assert.Equal(InstallationState.Installed, installation.State);
        Assert.Equal("1.4.0", installation.InstalledVersion);
        Assert.Null(installation.DisabledAt);
    }

    [Fact]
    public void EnablingSomethingAlreadyEnabled_IsNotAnError()
    {
        var (installation, _) = Installed();

        installation.Enable(Now);

        Assert.Equal(InstallationState.Installed, installation.State);
    }

    [Fact]
    public void AnUninstall_RemovesTheCodeAndKeepsTheDataForTheRetentionWindow()
    {
        var (installation, _) = Installed("1.4.0");
        var jobId = Guid.CreateVersion7();

        installation.BeginUninstall(jobId, Now);
        Assert.Equal(InstallationState.Uninstalling, installation.State);

        installation.MarkUninstalled(jobId, dataRetentionDays: 30, Now);

        Assert.Equal(InstallationState.NotInstalled, installation.State);
        Assert.Null(installation.InstalledVersion);
        Assert.Equal(Now, installation.UninstalledAt);
        Assert.Equal(Now.AddDays(30), installation.DataRetainedUntil);
    }

    [Fact]
    public void ADisabledFeature_CanStillBeUninstalled()
    {
        var (installation, _) = Installed();
        installation.Disable(Now);

        installation.BeginUninstall(Guid.CreateVersion7(), Now);

        Assert.Equal(InstallationState.Uninstalling, installation.State);
    }

    [Fact]
    public void AFailedFeature_CanBeUninstalledToGetOutOfTheWay()
    {
        var installation = New();
        var jobId = Guid.CreateVersion7();
        installation.QueueJob(jobId, VersionId, "1.0.0", Now);
        installation.BeginWork(jobId, Now);
        installation.MarkFailed(jobId, "install.failed", "Package install failed.", RollbackOutcome.RolledBack, Now);

        installation.BeginUninstall(Guid.CreateVersion7(), Now);

        Assert.Equal(InstallationState.Uninstalling, installation.State);
    }

    [Fact]
    public void ZeroRetention_PurgesImmediatelyRatherThanNever()
    {
        var (installation, _) = Installed();
        var jobId = Guid.CreateVersion7();
        installation.BeginUninstall(jobId, Now);

        installation.MarkUninstalled(jobId, dataRetentionDays: 0, Now);

        Assert.Equal(Now, installation.DataRetainedUntil);
    }

    [Fact]
    public void ANegativeRetentionWindow_IsRefused()
    {
        var (installation, _) = Installed();
        var jobId = Guid.CreateVersion7();
        installation.BeginUninstall(jobId, Now);

        Assert.Throws<DomainException>(() => installation.MarkUninstalled(jobId, -1, Now));
    }

    [Fact]
    public void PurgingClearsTheRetentionDeadline()
    {
        var (installation, _) = Installed();
        var jobId = Guid.CreateVersion7();
        installation.BeginUninstall(jobId, Now);
        installation.MarkUninstalled(jobId, 30, Now);

        installation.MarkPurged(Now.AddDays(31));

        Assert.Null(installation.DataRetainedUntil);
    }

    [Fact]
    public void SomethingStillInstalled_CannotBePurged()
    {
        var (installation, _) = Installed();

        Assert.Throws<DomainException>(() => installation.MarkPurged(Now));
    }

    // --- Illegal transitions ------------------------------------------------

    [Fact]
    public void OnlyOneJobAtATime()
    {
        var installation = New();
        installation.QueueJob(Guid.CreateVersion7(), VersionId, "1.0.0", Now);

        var exception = Assert.Throws<DomainException>(() =>
            installation.QueueJob(Guid.CreateVersion7(), VersionId, "1.1.0", Now));

        Assert.Equal(DomainErrorCategory.Conflict, exception.Category);
    }

    [Fact]
    public void WorkCannotBeginTwice()
    {
        var installation = New();
        var jobId = Guid.CreateVersion7();
        installation.QueueJob(jobId, VersionId, "1.0.0", Now);
        installation.BeginWork(jobId, Now);

        Assert.Throws<DomainException>(() => installation.BeginWork(jobId, Now));
    }

    [Fact]
    public void SomethingNotInFlight_CannotBeMarkedInstalled()
    {
        var installation = New();

        Assert.Throws<DomainException>(() => installation.MarkInstalled(Guid.CreateVersion7(), Now));
    }

    [Fact]
    public void SomethingNotInFlight_CannotFail()
    {
        var (installation, _) = Installed();

        Assert.Throws<DomainException>(() =>
            installation.MarkFailed(Guid.CreateVersion7(), "x", "y", RollbackOutcome.NotAttempted, Now));
    }

    [Fact]
    public void SomethingNotInstalled_CannotBeDisabled()
    {
        var installation = New();

        Assert.Throws<DomainException>(() => installation.Disable(Now));
    }

    [Fact]
    public void SomethingNotInstalled_CannotBeUninstalled()
    {
        var installation = New();

        Assert.Throws<DomainException>(() => installation.BeginUninstall(Guid.CreateVersion7(), Now));
    }

    [Fact]
    public void SomethingInFlight_CannotBeUninstalledUnderneathItsJob()
    {
        var installation = New();
        installation.QueueJob(Guid.CreateVersion7(), VersionId, "1.0.0", Now);

        Assert.Throws<DomainException>(() => installation.BeginUninstall(Guid.CreateVersion7(), Now));
    }

    [Fact]
    public void AFailedInstallation_CannotBeEnabledIntoService()
    {
        var installation = New();
        var jobId = Guid.CreateVersion7();
        installation.QueueJob(jobId, VersionId, "1.0.0", Now);
        installation.BeginWork(jobId, Now);
        installation.MarkFailed(jobId, "x", "y", RollbackOutcome.NotAttempted, Now);

        Assert.Throws<DomainException>(() => installation.Enable(Now));
    }

    // --- Stale reports ------------------------------------------------------

    [Fact]
    public void AReportFromAJobThatIsNoLongerCurrent_IsRefused()
    {
        // The case: a job times out, is replaced, and the original agent finally
        // reports. Applying it would let a dead job overwrite a live one.
        var installation = New();
        var current = Guid.CreateVersion7();
        installation.QueueJob(current, VersionId, "1.0.0", Now);
        installation.BeginWork(current, Now);

        var stale = Guid.CreateVersion7();
        var exception = Assert.Throws<DomainException>(() => installation.MarkInstalled(stale, Now));

        Assert.Equal(DomainErrorCategory.Conflict, exception.Category);
    }

    [Fact]
    public void ATransitionMustNameItsJob()
    {
        var installation = New();
        installation.QueueJob(Guid.CreateVersion7(), VersionId, "1.0.0", Now);

        Assert.Throws<DomainException>(() => installation.BeginWork(Guid.Empty, Now));
    }

    // --- Blocking reason and health ----------------------------------------

    [Fact]
    public void AnEntitledButUninstallableFeature_CarriesTheReasonWithoutChangingState()
    {
        // "Entitled, not installed, no reason given" is the state that generates
        // support tickets.
        var installation = New();

        installation.RecordBlockingReason("The store runs 3.2.0 and the feature requires >=4.0.0.", Now);

        Assert.Equal(InstallationState.NotInstalled, installation.State);
        Assert.Contains(">=4.0.0", installation.BlockingReason!, StringComparison.Ordinal);
    }

    [Fact]
    public void ASuccessfulJob_ClearsAStaleBlockingReason()
    {
        var installation = New();
        installation.RecordBlockingReason("Incompatible.", Now);

        installation.QueueJob(Guid.CreateVersion7(), VersionId, "1.0.0", Now);

        Assert.Null(installation.BlockingReason);
    }

    [Fact]
    public void HealthIsRecordedFromTheFeaturesOwnCheck()
    {
        var (installation, _) = Installed();

        installation.RecordHealth(FeatureHealth.Degraded, Now);

        Assert.Equal(FeatureHealth.Degraded, installation.Health);
        Assert.Equal(Now, installation.LastHealthCheckAt);
    }

    [Fact]
    public void AFailedJob_ResetsHealthToUnknownRatherThanLeavingItHealthy()
    {
        var (installation, _) = Installed();
        installation.RecordHealth(FeatureHealth.Healthy, Now);

        var jobId = Guid.CreateVersion7();
        installation.QueueJob(jobId, Guid.CreateVersion7(), "2.0.0", Now);
        installation.BeginWork(jobId, Now);
        installation.MarkFailed(jobId, "x", "y", RollbackOutcome.RolledBack, Now);

        Assert.Equal(FeatureHealth.Unknown, installation.Health);
    }

    // --- Disabling something whose last job failed -------------------------

    [Fact]
    public void AnInstallationWhoseUpgradeFailed_CanStillBeDisabled()
    {
        var (installation, _) = Installed("1.0.0");

        var upgrade = Guid.CreateVersion7();
        installation.QueueJob(upgrade, VersionId, "1.1.0", Now);
        installation.BeginWork(upgrade, Now);
        installation.MarkFailed(upgrade, "digest.mismatch", "The artifact did not match.", RollbackOutcome.NotAttempted, Now);

        Assert.Equal(InstallationState.Failed, installation.State);
        Assert.Equal("1.0.0", installation.InstalledVersion);

        installation.Disable(Now);

        // The store is running 1.0.0 and always was — an upgrade that failed at
        // `verify` never touched it. Refusing to disable it meant the store ran
        // the Disable job, reported it, and had the report rejected, so the job
        // sat in `Running` for ever while the Feature went on serving for a
        // customer who had stopped paying.
        Assert.Equal(InstallationState.Disabled, installation.State);
    }

    [Fact]
    public void AFirstInstallThatFailed_HasNothingToDisable()
    {
        var installation = New();
        var jobId = Guid.CreateVersion7();

        installation.QueueJob(jobId, VersionId, "1.0.0", Now);
        installation.BeginWork(jobId, Now);
        installation.MarkFailed(jobId, "fetch.failed", "The artifact could not be downloaded.", RollbackOutcome.NotAttempted, Now);

        Assert.Null(installation.InstalledVersion);

        // Failed and never installed. There is no version of this on the store,
        // so "disable it" is an instruction about nothing, and saying so is
        // better than sending an agent to switch off something that was never
        // there.
        Assert.Throws<DomainException>(() => installation.Disable(Now));
    }
}
