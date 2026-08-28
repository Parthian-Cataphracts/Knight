using FeatureDelivery.Domain;
using Knight.Domain.Exceptions;
using Xunit;

namespace Knight.UnitTests.ControlPlane;

/// <summary>
/// Job mechanics: the pipeline, idempotent step reporting, claims and timeouts.
///
/// The idempotency tests are the ones that matter most. An agent reporting a step
/// it already completed is not a bug — it is what happens every time a reply is
/// lost — and a job that treats the second report as a second execution is a job
/// that runs a migration twice.
/// </summary>
public sealed class FeatureInstallationJobTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 19, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan ClaimTimeout = TimeSpan.FromMinutes(10);

    private static FeatureInstallationJob Job(JobType type = JobType.Install, int maxAttempts = 3) =>
        FeatureInstallationJob.Queue(
            Guid.CreateVersion7(),
            Now,
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            "analytics-core",
            type,
            type is JobType.Uninstall ? null : Guid.CreateVersion7(),
            type is JobType.Uninstall ? null : "1.4.0",
            "idem-1",
            "corr-1",
            Guid.CreateVersion7(),
            JobTrigger.Manual,
            maxAttempts);

    private static FeatureInstallationJob Running(JobType type = JobType.Install)
    {
        var job = Job(type);
        job.Claim(Now, ClaimTimeout);
        return job;
    }

    // --- Queueing ----------------------------------------------------------

    [Fact]
    public void AQueuedJob_KnowsItsWholePipelineUpFront()
    {
        var job = Job();

        Assert.Equal(JobState.Queued, job.State);
        Assert.Equal(JobPipeline.StepsFor(JobType.Install).Count, job.TotalStepCount);
        Assert.Equal(0, job.CompletedStepCount);
        Assert.Equal(JobPipeline.Preflight, job.NextStep());
    }

    [Fact]
    public void AnInstallJobWithoutATargetVersion_IsRefused()
    {
        var exception = Assert.Throws<DomainException>(() => FeatureInstallationJob.Queue(
            Guid.CreateVersion7(), Now, Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(),
            Guid.CreateVersion7(), "analytics-core", JobType.Install, null, null,
            "idem-1", "corr-1", Guid.CreateVersion7(), JobTrigger.Manual));

        Assert.Equal(DomainErrorCategory.Validation, exception.Category);
    }

    [Fact]
    public void AnUninstallJob_NeedsNoTargetVersion()
    {
        var job = Job(JobType.Uninstall);

        Assert.Null(job.TargetVersion);
        Assert.Equal(JobPipeline.Disable, job.NextStep());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(11)]
    public void AnAbsurdRetryBudget_IsRefused(int maxAttempts)
    {
        Assert.Throws<DomainException>(() => Job(maxAttempts: maxAttempts));
    }

    // --- Claiming ----------------------------------------------------------

    [Fact]
    public void ClaimingAJob_StartsItAndSetsADeadline()
    {
        var job = Job();

        job.Claim(Now, ClaimTimeout);

        Assert.Equal(JobState.Running, job.State);
        Assert.Equal(Now, job.ClaimedAt);
        Assert.Equal(Now.Add(ClaimTimeout), job.ClaimExpiresAt);
        Assert.Equal(1, job.AttemptCount);
    }

    [Fact]
    public void AJobCannotBeClaimedTwice()
    {
        var job = Running();

        Assert.Throws<DomainException>(() => job.Claim(Now, ClaimTimeout));
    }

    // --- Step reporting -----------------------------------------------------

    [Fact]
    public void StepsAreRecordedInPipelineOrder()
    {
        var job = Running();

        job.ReportStep(JobPipeline.Preflight, StepStatus.Succeeded, Now);
        Assert.Equal(JobPipeline.Fetch, job.NextStep());

        job.ReportStep(JobPipeline.Fetch, StepStatus.Succeeded, Now);
        Assert.Equal(JobPipeline.Verify, job.NextStep());

        Assert.Equal(2, job.CompletedStepCount);
    }

    [Fact]
    public void ReportingTheSameStepTwice_UpdatesItRatherThanAppending()
    {
        // The lost-reply case. Two rows for one step would make "3 of 10" wrong
        // and would look like the step ran twice.
        var job = Running();

        job.ReportStep(JobPipeline.Preflight, StepStatus.Running, Now);
        job.ReportStep(JobPipeline.Preflight, StepStatus.Succeeded, Now.AddSeconds(2), output: "ok");

        var step = Assert.Single(job.Steps);
        Assert.Equal(StepStatus.Succeeded, step.Status);
        Assert.Equal("ok", step.Output);
        Assert.Equal(2, step.ReportCount);
        Assert.Equal(1, job.CompletedStepCount);
    }

    [Fact]
    public void AStepThatAlreadySucceeded_IsNeverDowngradedByARepeatReport()
    {
        // An agent that succeeded, lost the network, and re-ran a step that had
        // already applied must not turn a working job into a failed one.
        var job = Running();
        job.ReportStep(JobPipeline.Preflight, StepStatus.Succeeded, Now);

        job.ReportStep(JobPipeline.Preflight, StepStatus.Failed, Now.AddSeconds(5), errorCode: "already-applied");

        var step = Assert.Single(job.Steps);
        Assert.Equal(StepStatus.Succeeded, step.Status);
        Assert.Equal(2, step.ReportCount);
    }

    [Fact]
    public void ARetriedJob_ResumesAtTheFirstUnfinishedStepRatherThanTheTop()
    {
        var job = Running();
        job.ReportStep(JobPipeline.Preflight, StepStatus.Succeeded, Now);
        job.ReportStep(JobPipeline.Fetch, StepStatus.Succeeded, Now);
        job.ReportStep(JobPipeline.Verify, StepStatus.Failed, Now, errorCode: "digest.mismatch");

        Assert.Equal(JobPipeline.Verify, job.NextStep());
    }

    [Fact]
    public void ASkippedStep_DoesNotBlockThePipeline()
    {
        var job = Running();
        foreach (var step in new[] { JobPipeline.Preflight, JobPipeline.Fetch, JobPipeline.Verify, JobPipeline.Backup, JobPipeline.Install })
        {
            job.ReportStep(step, StepStatus.Succeeded, Now);
        }

        // A manifest with no extensions and no migrations skips both steps; the
        // job must not sit waiting for a step that will never run. Most Features
        // skip the first of these and a good few skip the second.
        job.ReportStep(JobPipeline.CreateExtensions, StepStatus.Skipped, Now);
        job.ReportStep(JobPipeline.Migrate, StepStatus.Skipped, Now);

        Assert.Equal(JobPipeline.Configure, job.NextStep());
        Assert.Equal(7, job.CompletedStepCount);
    }

    [Fact]
    public void AnUnknownStepName_IsRefused()
    {
        // A rogue or outdated agent must not be able to invent a step that KNIGHT
        // then records as progress.
        var job = Running();

        var exception = Assert.Throws<DomainException>(() =>
            job.ReportStep("rm-rf-slash", StepStatus.Succeeded, Now));

        Assert.Equal(DomainErrorCategory.Validation, exception.Category);
    }

    [Fact]
    public void AStepFromAnotherJobType_IsRefused()
    {
        var job = Running(JobType.Disable);

        Assert.Throws<DomainException>(() => job.ReportStep(JobPipeline.Migrate, StepStatus.Succeeded, Now));
    }

    [Fact]
    public void AJobThatIsNotRunning_CannotReportProgress()
    {
        var job = Job();

        Assert.Throws<DomainException>(() => job.ReportStep(JobPipeline.Preflight, StepStatus.Succeeded, Now));
    }

    [Fact]
    public void StepOutputIsCappedSoOneVerboseMigrationCannotFillTheTable()
    {
        var job = Running();

        job.ReportStep(JobPipeline.Migrate, StepStatus.Succeeded, Now, output: new string('x', 20_000));

        Assert.Equal(JobStepResult.MaxOutputLength, Assert.Single(job.Steps).Output!.Length);
    }

    // --- Completion ---------------------------------------------------------

    [Fact]
    public void ASucceededJob_IsFinished()
    {
        var job = Running();

        job.Succeed(Now);

        Assert.Equal(JobState.Succeeded, job.State);
        Assert.True(job.IsFinished);
        Assert.Null(job.ClaimExpiresAt);
    }

    [Fact]
    public void AFailedJob_RecordsItsRollbackOutcome()
    {
        var job = Running();

        job.Fail("migrate.irreversible", "Migration 0003 cannot be reversed.", RollbackOutcome.ManualInterventionRequired, Now);

        Assert.Equal(JobState.Failed, job.State);
        Assert.Equal(RollbackOutcome.ManualInterventionRequired, job.RollbackOutcome);
        Assert.True(job.IsFinished);
    }

    [Fact]
    public void AFinishedJob_CannotBeFinishedAgain()
    {
        var job = Running();
        job.Succeed(Now);

        Assert.Throws<DomainException>(() => job.Succeed(Now));
        Assert.Throws<DomainException>(() => job.Fail("x", "y", RollbackOutcome.NotAttempted, Now));
        Assert.Throws<DomainException>(() => job.Cancel("changed my mind", Now));
    }

    [Fact]
    public void AQueuedJob_CanBeCancelledBeforeAnAgentEverSeesIt()
    {
        var job = Job();

        job.Cancel("The entitlement was revoked before the agent polled.", Now);

        Assert.Equal(JobState.Cancelled, job.State);
        Assert.Equal("job.cancelled", job.FailureCode);
    }

    // --- Timeouts -----------------------------------------------------------

    [Fact]
    public void AnAbandonedJob_ReturnsToTheQueueWhileItHasAttemptsLeft()
    {
        var job = Running();

        Assert.False(job.IsClaimExpired(Now.AddMinutes(5)));
        Assert.True(job.IsClaimExpired(Now.AddMinutes(11)));

        var requeued = job.TimeOut(Now.AddMinutes(11));

        Assert.True(requeued);
        Assert.Equal(JobState.Queued, job.State);
        Assert.Null(job.ClaimedAt);
        Assert.Equal(1, job.AttemptCount);
    }

    [Fact]
    public void AJobThatKeepsHanging_EventuallyFailsRatherThanRetryingForever()
    {
        // An install that hangs three times is not going to work on the fourth,
        // and a queue that keeps retrying it never runs the store's real work.
        var job = Job(maxAttempts: 2);

        job.Claim(Now, ClaimTimeout);
        Assert.True(job.TimeOut(Now.AddMinutes(11)));

        job.Claim(Now.AddMinutes(12), ClaimTimeout);
        Assert.False(job.TimeOut(Now.AddMinutes(30)));

        Assert.Equal(JobState.Failed, job.State);
        Assert.Equal("job.timeout", job.FailureCode);
        Assert.Equal(2, job.AttemptCount);
    }

    [Fact]
    public void AJobThatIsNotRunning_CannotTimeOut()
    {
        var job = Job();

        Assert.Throws<DomainException>(() => job.TimeOut(Now));
    }

    // --- Rollback ordering ---------------------------------------------------

    [Fact]
    public void RollbackWalksTheSucceededStepsBackwards()
    {
        var job = Running();
        job.ReportStep(JobPipeline.Preflight, StepStatus.Succeeded, Now);
        job.ReportStep(JobPipeline.Fetch, StepStatus.Succeeded, Now);
        job.ReportStep(JobPipeline.Verify, StepStatus.Succeeded, Now);
        job.ReportStep(JobPipeline.Backup, StepStatus.Failed, Now, errorCode: "disk.full");

        var reverse = job.SucceededStepsInReverse();

        Assert.Equal(
            [JobPipeline.Verify, JobPipeline.Fetch, JobPipeline.Preflight],
            reverse.Select(step => step.Name));
    }

    // --- The pipeline itself -------------------------------------------------

    [Fact]
    public void EveryJobTypeHasAPipeline()
    {
        foreach (var type in Enum.GetValues<JobType>())
        {
            Assert.NotEmpty(JobPipeline.StepsFor(type));
        }
    }

    [Fact]
    public void AnInstallVerifiesTheArtifactBeforeItInstallsIt()
    {
        // Signature and digest verification must precede installation, or the
        // check is decoration (ADR 0015).
        var steps = JobPipeline.StepsFor(JobType.Install);

        Assert.True(steps.ToList().IndexOf(JobPipeline.Verify) < steps.ToList().IndexOf(JobPipeline.Install));
        Assert.True(steps.ToList().IndexOf(JobPipeline.Fetch) < steps.ToList().IndexOf(JobPipeline.Verify));
    }

    [Fact]
    public void AnInstallTakesABackupBeforeItMigrates()
    {
        var steps = JobPipeline.StepsFor(JobType.Install).ToList();

        Assert.True(steps.IndexOf(JobPipeline.Backup) < steps.IndexOf(JobPipeline.Migrate));
    }

    [Fact]
    public void ARollbackReversesTheMigrationsBeforeItPutsTheOldPackageBack()
    {
        var steps = JobPipeline.StepsFor(JobType.Rollback).ToList();

        // Django can only unapply a migration whose file it can still see.
        // Restoring the previous package first removes the newer migration from
        // disk, so the reverse that follows finds nothing to undo and leaves the
        // schema where the upgrade left it — the code rolled back and the
        // database not.
        //
        // The delivery drill found this on the first rollback across two
        // versions whose migrations actually differed. It is an ordering, so it
        // is asserted as one: nothing else in the suite would notice it moving.
        Assert.True(steps.IndexOf(JobPipeline.ReverseMigrate) < steps.IndexOf(JobPipeline.RestorePackage));
    }

    [Fact]
    public void AnUninstallDisablesBeforeItRemovesAnything()
    {
        var steps = JobPipeline.StepsFor(JobType.Uninstall).ToList();

        Assert.True(steps.IndexOf(JobPipeline.Disable) < steps.IndexOf(JobPipeline.RemovePackage));
    }

    [Fact]
    public void OnlyTheMigrationStepsTouchTheDatabase()
    {
        Assert.True(JobPipeline.TouchesDatabase(JobPipeline.Migrate));
        Assert.True(JobPipeline.TouchesDatabase(JobPipeline.ReverseMigrate));
        Assert.False(JobPipeline.TouchesDatabase(JobPipeline.Install));
        Assert.False(JobPipeline.TouchesDatabase(JobPipeline.Reload));

        // Creating an extension writes to the database and still belongs on the
        // false side: nothing ever drops one, so it can neither need a reverse
        // nor stop one (docs/adr/0031).
        Assert.False(JobPipeline.TouchesDatabase(JobPipeline.CreateExtensions));
    }

    [Fact]
    public void AnInstallCreatesItsExtensionsBeforeItMigrates()
    {
        // The ordering is the reason the step exists. A store's database user is
        // routinely not allowed to create an extension, and learning that before
        // a migration has applied is the difference between a job that failed and
        // one that has to be finished by hand (docs/adr/0031).
        var steps = JobPipeline.StepsFor(JobType.Install).ToList();

        Assert.True(steps.IndexOf(JobPipeline.Install) < steps.IndexOf(JobPipeline.CreateExtensions));
        Assert.True(steps.IndexOf(JobPipeline.CreateExtensions) < steps.IndexOf(JobPipeline.Migrate));
    }

    [Fact]
    public void NoRollbackPathEverDropsAnExtension()
    {
        // Stated as a test because it is the whole decision: a rollback cannot
        // know whether another Feature has started using the extension, so it
        // leaves it (docs/adr/0031).
        Assert.DoesNotContain(JobPipeline.CreateExtensions, JobPipeline.StepsFor(JobType.Rollback));
        Assert.DoesNotContain(JobPipeline.CreateExtensions, JobPipeline.StepsFor(JobType.Uninstall));
    }

    // --- Delivering a service rather than a package -------------------------

    [Fact]
    public void AnExternalServiceInstallNeitherMigratesNorCreatesExtensionsNorReloads()
    {
        var steps = JobPipeline.StepsFor(JobType.Install, DeliveryArchitecture.ExternalService);

        // Each absence is a fact about the architecture rather than a shortcut:
        // no database to add an extension to, no schema to migrate, and nothing
        // loaded into the store's process that a restart would replace
        // (adr/0033).
        Assert.DoesNotContain(JobPipeline.CreateExtensions, steps);
        Assert.DoesNotContain(JobPipeline.Migrate, steps);
        Assert.DoesNotContain(JobPipeline.Reload, steps);
    }

    [Fact]
    public void AnExternalServiceInstallStillBacksUp()
    {
        var steps = JobPipeline.StepsFor(JobType.Install, DeliveryArchitecture.ExternalService);

        // There is no package to keep, and there is still something to keep:
        // the registration this install is about to replace. The rollback
        // restores from that local copy rather than fetching, because a
        // rollback job names the version it is rolling *to* and carries the
        // artifact of the one it is rolling *from* — a store that fetched
        // would reinstall the version it was trying to leave.
        //
        // This started out absent, on the reasoning that an external Feature
        // has nothing to back up. It has: not code, but the answer to "what was
        // registered before this".
        Assert.Contains(JobPipeline.Backup, steps);
        Assert.Contains(JobPipeline.RestorePackage, JobPipeline.StepsFor(JobType.Rollback, DeliveryArchitecture.ExternalService));
    }

    [Fact]
    public void NoExternalPipelineNamesAVerbTheInProcessOneDoesNot()
    {
        var known = new[]
        {
            JobType.Install, JobType.Upgrade, JobType.Rollback,
            JobType.Enable, JobType.Disable, JobType.Uninstall, JobType.ApplyConfiguration,
        };

        var vocabulary = known
            .SelectMany(type => JobPipeline.StepsFor(type, DeliveryArchitecture.InProcess))
            .Distinct(StringComparer.Ordinal)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var type in known)
        {
            foreach (var step in JobPipeline.StepsFor(type, DeliveryArchitecture.ExternalService))
            {
                // The whole reason this pivot does not break the three store
                // agents. A store that meets an unknown step refuses the job,
                // and phase 20 found the node store had been missing three
                // verbs for three phases without anybody noticing. Adding a new
                // one here would have broken every store on the day it shipped.
                Assert.True(
                    vocabulary.Contains(step),
                    $"The external pipeline for {type} names '{step}', which no store has ever been asked to perform.");
            }
        }
    }

    [Fact]
    public void AnExternalServiceRollbackHasNothingToReverse()
    {
        var steps = JobPipeline.StepsFor(JobType.Rollback, DeliveryArchitecture.ExternalService);

        // The single largest operational difference between the two
        // architectures. The rollback that mattered for code — reverse the
        // migrations before restoring the package — has no counterpart, because
        // there is nothing in the store's database to reverse.
        Assert.DoesNotContain(JobPipeline.ReverseMigrate, steps);
        Assert.Contains(JobPipeline.RestorePackage, steps);
    }

    [Fact]
    public void TheStepCountAJobReportsAgainstIsItsOwnPipelines()
    {
        var external = Queue(JobType.Install, DeliveryArchitecture.ExternalService);
        var inProcess = Queue(JobType.Install, DeliveryArchitecture.InProcess);

        // A job counting eleven steps while running seven would show a progress
        // bar that never finishes, on every external install, for ever.
        Assert.Equal(
            JobPipeline.StepsFor(JobType.Install, DeliveryArchitecture.ExternalService).Count,
            external.TotalStepCount);

        Assert.NotEqual(inProcess.TotalStepCount, external.TotalStepCount);
    }

    [Fact]
    public void AJobRefusesAStepItsOwnPipelineDoesNotName()
    {
        var job = Queue(JobType.Install, DeliveryArchitecture.ExternalService);
        job.Claim(Now, ClaimTimeout);

        // `migrate` is a real verb and a real step of the *other* pipeline. An
        // external job reporting it would be a store that had run a migration
        // for a Feature with no schema, and KNIGHT should not record that as
        // having happened.
        Assert.Throws<DomainException>(() =>
            job.ReportStep(JobPipeline.Migrate, StepStatus.Succeeded, Now));
    }

    private static FeatureInstallationJob Queue(JobType type, DeliveryArchitecture architecture) =>
        FeatureInstallationJob.Queue(
            Guid.CreateVersion7(),
            Now,
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            "subscriptions",
            type,
            Guid.CreateVersion7(),
            "2.0.0",
            $"{type}:{Guid.NewGuid():n}",
            Guid.NewGuid().ToString("n"),
            Guid.CreateVersion7(),
            JobTrigger.Manual,
            architecture: architecture);
}
