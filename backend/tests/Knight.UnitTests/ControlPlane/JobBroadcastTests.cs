using Knight.Application.Abstractions.ControlPlane;
using Knight.Application.Abstractions.Observability;
using Knight.Application.Abstractions.Security;
using Knight.Application.Abstractions.Time;
using FeatureDelivery;
using FeatureDelivery.Domain;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;

namespace Knight.UnitTests.ControlPlane;

/// <summary>
/// An installation job tells whoever is watching that it moved.
///
/// This is the one operation in KNIGHT an operator watches happen: it runs for
/// minutes on somebody else's machine and can fail halfway. The tests assert
/// what the delivery engine hands the realtime channel, not what a browser does
/// with it — the browser's half is a subscription that refetches, and the part
/// that can silently regress is this one.
///
/// The recording notifier stands in for the hub. Using the real one would test
/// SignalR rather than KNIGHT, and would make the assertion depend on a
/// websocket being open.
/// </summary>
public sealed class JobBroadcastTests
{
    /// <summary>Captures what the delivery engine asked to broadcast, in order.</summary>
    private sealed class RecordingNotifier : IRealtimeNotifier
    {
        public List<RealtimeMessage> Messages { get; } = [];

        public Task BroadcastAsync(RealtimeMessage message, CancellationToken cancellationToken)
        {
            Messages.Add(message);

            return Task.CompletedTask;
        }
    }

    /// <summary>A notifier that always fails, to prove a dropped channel cannot fail the report.</summary>
    private sealed class FailingNotifier : IRealtimeNotifier
    {
        public Task BroadcastAsync(RealtimeMessage message, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("No listener.");
    }

    [Fact]
    public async Task ReportingAStepBroadcastsProgressToTheJobsCustomer()
    {
        var notifier = new RecordingNotifier();
        var (service, job) = Build(notifier);

        await service.ReportStepAsync(
            job.StoreId,
            job.Id,
            new StepReport("preflight", "Succeeded", null, null, 120),
            CancellationToken.None);

        var message = Assert.Single(notifier.Messages);

        Assert.Equal("jobProgress", message.Event);

        // Addressed to the job's customer, so the hub's routing rule applies:
        // that customer and platform staff, and nobody else (adr/0022).
        Assert.Equal(job.CustomerId, message.CustomerId);
    }

    [Fact]
    public async Task EveryStepIsBroadcastSoAProgressBarCanFollowIt()
    {
        var notifier = new RecordingNotifier();
        var (service, job) = Build(notifier);

        foreach (var step in JobPipeline.StepsFor(job.Type).Take(3))
        {
            await service.ReportStepAsync(
                job.StoreId,
                job.Id,
                new StepReport(step, "Succeeded", null, null, 10),
                CancellationToken.None);
        }

        Assert.Equal(3, notifier.Messages.Count);
        Assert.All(notifier.Messages, message => Assert.Equal("jobProgress", message.Event));
    }

    [Fact]
    public async Task ABroadcastThatFailsDoesNotFailTheAgentsReport()
    {
        // The trade stated plainly: realtime is an improvement on polling, and a
        // dropped websocket must never cost an agent its step report — that
        // report is the only record the job ever produces.
        var (service, job) = Build(new FailingNotifier());

        var exception = await Record.ExceptionAsync(() => service.ReportStepAsync(
            job.StoreId,
            job.Id,
            new StepReport("preflight", "Succeeded", null, null, 120),
            CancellationToken.None));

        Assert.Null(exception);
    }

    /// <summary>
    /// Builds the service over substituted repositories with one queued,
    /// claimed job. Deliberately not a database test: what is under test is
    /// which messages the service emits, and a real schema would add nothing but
    /// setup.
    /// </summary>
    private static (IAgentJobService Service, FeatureInstallationJob Job) Build(IRealtimeNotifier notifier)
    {
        var now = DateTimeOffset.UtcNow;

        var job = FeatureInstallationJob.Queue(
            Guid.CreateVersion7(),
            now,
            storeId: Guid.NewGuid(),
            customerId: Guid.NewGuid(),
            installationId: Guid.NewGuid(),
            featureId: Guid.NewGuid(),
            featureSlug: "knight-feature-analytics-core",
            type: JobType.Install,
            targetVersionId: Guid.NewGuid(),
            targetVersion: "1.0.0",
            idempotencyKey: Guid.NewGuid().ToString("n"),
            correlationId: Guid.NewGuid().ToString("n"),
            requestedBy: Guid.NewGuid(),
            trigger: JobTrigger.Manual);

        job.Claim(now, TimeSpan.FromMinutes(15));

        var jobs = Substitute.For<IFeatureInstallationJobRepository>();
        jobs.GetByIdAsync(job.Id, Arg.Any<CancellationToken>()).Returns(job);

        var clock = Substitute.For<IDateTimeProvider>();
        clock.UtcNow.Returns(now);

        var service = new AgentJobService(
            jobs,
            Substitute.For<IFeatureInstallationRepository>(),
            Substitute.For<IFeatureConfigurationRepository>(),
            Substitute.For<IFeatureVersionReader>(),
            Substitute.For<IFeatureArtifactStore>(),
            Substitute.For<ISecretProtector>(),
            Substitute.For<IAuditTrail>(),
            Substitute.For<IKnightMetrics>(),
            notifier,
            clock,
            Options.Create(new FeatureDeliveryOptions()));

        return (service, job);
    }
}
