using Knight.Domain.Exceptions;
using Provisioning.Domain;
using Xunit;

namespace Knight.UnitTests.ControlPlane;

/// <summary>
/// The provisioning state machine.
///
/// Two properties carry the weight here. A run is resumable — re-evaluating a
/// step that is still waiting must not create a second row or lose the steps
/// before it — and a manual step is the only kind a person may tick off, because
/// an operator asserting "the health check passed" would put a store into Active
/// without anything having checked.
/// </summary>
public sealed class ProvisioningJobTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 20, 9, 0, 0, TimeSpan.Zero);

    private static ProvisioningJob Job(ProvisioningKind kind = ProvisioningKind.Provision) =>
        ProvisioningJob.Start(
            Guid.CreateVersion7(),
            Now,
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            kind,
            "idem-1",
            "corr-1",
            Guid.CreateVersion7());

    [Fact]
    public void ANewRun_StartsOnTheFirstStepOfItsPipeline()
    {
        var job = Job();

        Assert.Equal(ProvisioningPipeline.Server, job.NextStep());
        Assert.Equal(ProvisioningPipeline.StepsFor(ProvisioningKind.Provision).Count, job.TotalStepCount);
        Assert.Equal(0, job.CompletedStepCount);
    }

    [Fact]
    public void ADeprovisioningRun_HasItsOwnPipeline()
    {
        var job = Job(ProvisioningKind.Deprovision);

        Assert.Equal(ProvisioningPipeline.DisableFeatures, job.NextStep());
    }

    [Fact]
    public void AWaitingStep_KeepsTheRunOnThatStep()
    {
        var job = Job();

        job.ReportStep(ProvisioningPipeline.Server, ProvisioningStepStatus.Waiting, Now, "No machine yet.");

        Assert.Equal(ProvisioningPipeline.Server, job.NextStep());
        Assert.Equal(0, job.CompletedStepCount);
    }

    [Fact]
    public void ReEvaluatingAWaitingStep_UpdatesItsRowRatherThanAddingOne()
    {
        var job = Job();

        job.ReportStep(ProvisioningPipeline.Server, ProvisioningStepStatus.Waiting, Now, "No machine yet.");
        var second = job.ReportStep(ProvisioningPipeline.Server, ProvisioningStepStatus.Waiting, Now, "Still no machine.");

        Assert.Null(second);
        Assert.Single(job.Steps);
        Assert.Equal(2, job.Steps.Single().ReportCount);
        Assert.Equal("Still no machine.", job.Steps.Single().Detail);
    }

    [Fact]
    public void ASucceededStep_IsNotDowngradedByALaterPass()
    {
        var job = Job();

        job.ReportStep(ProvisioningPipeline.Server, ProvisioningStepStatus.Succeeded, Now, "On server A.");
        job.ReportStep(ProvisioningPipeline.Server, ProvisioningStepStatus.Waiting, Now, "Cannot see the server.");

        Assert.Equal(ProvisioningStepStatus.Succeeded, job.Steps.Single().Status);
    }

    [Fact]
    public void AStepThatIsNotPartOfThePipeline_IsRefused()
    {
        var job = Job();

        Assert.Throws<DomainException>(() =>
            job.ReportStep("run-whatever-i-say", ProvisioningStepStatus.Succeeded, Now));
    }

    [Fact]
    public void ARunSittingOnAManualStep_SaysItIsWaitingForAPerson()
    {
        var job = Job();

        job.ReportStep(ProvisioningPipeline.Server, ProvisioningStepStatus.Waiting, Now, "No machine yet.");

        Assert.Equal(ProvisioningState.AwaitingOperator, job.State);
        Assert.True(job.IsAwaitingOperator);
    }

    [Fact]
    public void AnOperator_CannotTickOffAStepKnightCarriesOutItself()
    {
        var job = Job();

        var refusal = Assert.Throws<DomainException>(() =>
            job.CompleteManualStep(ProvisioningPipeline.HealthCheck, Guid.CreateVersion7(), null, Now));

        Assert.Contains("cannot be ticked off by hand", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnOperator_CanCompleteAManualStep()
    {
        var job = Job();
        var actor = Guid.CreateVersion7();

        job.CompleteManualStep(ProvisioningPipeline.Server, actor, "Built by hand on rack 3.", Now);

        var step = job.Steps.Single();
        Assert.Equal(ProvisioningStepStatus.Succeeded, step.Status);
        Assert.Equal(actor, step.CompletedBy);
        Assert.Equal(ProvisioningPipeline.Instance, job.NextStep());
    }

    [Fact]
    public void AFailedStep_FailsTheRunAndNamesTheStep()
    {
        var job = Job();

        job.CompleteManualStep(ProvisioningPipeline.Server, Guid.CreateVersion7(), null, Now);
        job.CompleteManualStep(ProvisioningPipeline.Instance, Guid.CreateVersion7(), null, Now);
        job.ReportStep(ProvisioningPipeline.StoreRecord, ProvisioningStepStatus.Failed, Now, "No store record.", "provisioning.store.missing");

        Assert.Equal(ProvisioningState.Failed, job.State);
        Assert.Equal("provisioning.store.missing", job.FailureCode);
        Assert.NotNull(job.CompletedAt);
    }

    [Fact]
    public void RetryingAFailedRun_ClearsOnlyTheFailedStep()
    {
        var job = Job();

        job.CompleteManualStep(ProvisioningPipeline.Server, Guid.CreateVersion7(), "On server A.", Now);
        job.ReportStep(ProvisioningPipeline.Instance, ProvisioningStepStatus.Failed, Now, "Image missing.", "provisioning.image.missing");

        job.Retry(Now);

        Assert.Equal(ProvisioningState.Running, job.State);
        Assert.Null(job.FailureCode);
        Assert.Equal(ProvisioningPipeline.Instance, job.NextStep());

        var server = job.Steps.Single(step => step.Name == ProvisioningPipeline.Server);
        Assert.Equal(ProvisioningStepStatus.Succeeded, server.Status);
    }

    [Fact]
    public void ARunWhoseEveryStepFinished_HasSucceeded()
    {
        var job = Job();

        foreach (var step in ProvisioningPipeline.StepsFor(ProvisioningKind.Provision))
        {
            job.ReportStep(step.Name, ProvisioningStepStatus.Succeeded, Now, "done");
        }

        Assert.Equal(ProvisioningState.Succeeded, job.State);
        Assert.Null(job.NextStep());
        Assert.NotNull(job.CompletedAt);
    }

    [Fact]
    public void AFinishedRun_RefusesFurtherReports()
    {
        var job = Job();

        foreach (var step in ProvisioningPipeline.StepsFor(ProvisioningKind.Provision))
        {
            job.ReportStep(step.Name, ProvisioningStepStatus.Succeeded, Now, "done");
        }

        Assert.Throws<DomainException>(() =>
            job.ReportStep(ProvisioningPipeline.HealthCheck, ProvisioningStepStatus.Failed, Now, "late"));
    }

    [Fact]
    public void OnlyADeprovisioningRun_HasARetentionWindow()
    {
        Assert.Throws<DomainException>(() => Job().RetainDataUntil(Now.AddDays(30), Now));

        var deprovision = Job(ProvisioningKind.Deprovision);
        deprovision.RetainDataUntil(Now.AddDays(30), Now);

        Assert.Equal(Now.AddDays(30), deprovision.RetainUntil);
    }
}
