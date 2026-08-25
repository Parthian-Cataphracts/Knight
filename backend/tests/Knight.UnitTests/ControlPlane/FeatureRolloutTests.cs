using FeatureDelivery.Domain;
using Knight.Domain.Exceptions;
using Xunit;

namespace Knight.UnitTests.ControlPlane;

/// <summary>
/// The staged rollout, which is the mitigation R16 in `docs/risks.md` asks for:
/// KNIGHT delivers executable code into customer production systems, so a bad
/// version must not be able to reach every store at once.
///
/// These are release-blocking for the same reason the isolation tests are. The
/// rules being checked — the canary is one store, a wave waits for the wave
/// before it, failures halt the rollout — are the entire mitigation. If they
/// hold only by convention then the mitigation does not exist.
/// </summary>
public sealed class FeatureRolloutTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 20, 12, 0, 0, TimeSpan.Zero);

    private static FeatureRollout Plan(int storeCount, int[]? percentages = null, int threshold = 1) =>
        FeatureRollout.Plan(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "advanced-promotions",
            Guid.NewGuid(),
            "1.1.0",
            Enumerable.Range(0, storeCount).Select(_ => Guid.NewGuid()).ToArray(),
            percentages ?? [50, 100],
            threshold,
            "ali@example.com",
            Now);

    [Fact]
    public void TheFirstWaveIsAlwaysASingleStore()
    {
        // The whole point. A percentage-based first wave on a large fleet is not
        // a canary, it is an incident with extra steps.
        foreach (var count in new[] { 1, 2, 10, 400 })
        {
            var rollout = Plan(count);

            Assert.True(rollout.Waves[0].IsCanary);
            Assert.Single(rollout.Waves[0].Targets);
        }
    }

    [Fact]
    public void EveryStoreLandsInExactlyOneWave()
    {
        var rollout = Plan(37, [10, 30, 100]);

        var placed = rollout.Waves.SelectMany(wave => wave.Targets.Select(target => target.StoreId)).ToArray();

        Assert.Equal(37, placed.Length);
        Assert.Equal(37, placed.Distinct().Count());
        Assert.Equal(37, rollout.TotalStores);
    }

    [Fact]
    public void PercentagesThatDoNotReachAHundredStillCoverEveryStore()
    {
        // A rollout that quietly left ten percent of stores on the old version
        // would be worse than one that refused to start.
        var rollout = Plan(20, [10, 20, 30]);

        Assert.Equal(20, rollout.TotalStores);
    }

    [Fact]
    public void ARolloutNeedsAtLeastOneStore()
    {
        var exception = Assert.Throws<DomainException>(() => FeatureRollout.Plan(
            Guid.NewGuid(), Guid.NewGuid(), "slug", Guid.NewGuid(), "1.0.0",
            [], [100], 1, "ali@example.com", Now));

        Assert.Contains("at least one store", exception.Message);
    }

    [Fact]
    public void AStoreMayNotAppearTwice()
    {
        var duplicate = Guid.NewGuid();

        Assert.Throws<DomainException>(() => FeatureRollout.Plan(
            Guid.NewGuid(), Guid.NewGuid(), "slug", Guid.NewGuid(), "1.0.0",
            [duplicate, duplicate], [100], 1, "ali@example.com", Now));
    }

    [Fact]
    public void TheSecondWaveIsNotOfferedUntilTheFirstHasFinished()
    {
        var rollout = Plan(6);
        rollout.Start(Now);

        var canary = rollout.NextWave()!;
        rollout.MarkWaveDispatched(canary.Id, Now);

        // Dispatched but not yet reported on: there is nothing to send next.
        Assert.Null(rollout.NextWave());

        rollout.RecordResult(canary.Targets[0].StoreId, succeeded: true, null, Now);

        var second = rollout.NextWave();
        Assert.NotNull(second);
        Assert.Equal(1, second!.Ordinal);
    }

    [Fact]
    public void AFailedCanaryHaltsTheRolloutAndOffersNoFurtherWaves()
    {
        var rollout = Plan(50, [25, 100]);
        rollout.Start(Now);

        var canary = rollout.NextWave()!;
        rollout.MarkWaveDispatched(canary.Id, Now);

        var halted = rollout.RecordResult(canary.Targets[0].StoreId, succeeded: false, "migration failed", Now);

        Assert.True(halted);
        Assert.Equal(RolloutState.Halted, rollout.State);
        Assert.Null(rollout.NextWave());

        // And the 49 stores behind the canary were never touched.
        Assert.Equal(0, rollout.SucceededStores);
        Assert.Equal(1, rollout.FailedStores);
    }

    [Fact]
    public void FailuresAreCountedAcrossTheWholeRolloutNotPerWave()
    {
        // Three failures spread one per wave is the same bad version as three in
        // one wave, and only a whole-rollout count notices the first shape.
        // The canary succeeds here on purpose: a failed canary halts regardless
        // of the threshold, so counting across waves has to be shown with the
        // failures after it.
        var rollout = Plan(12, [50, 100], threshold: 3);
        rollout.Start(Now);

        var canary = rollout.NextWave()!;
        rollout.MarkWaveDispatched(canary.Id, Now);
        rollout.RecordResult(canary.Targets[0].StoreId, succeeded: true, null, Now);

        var second = rollout.NextWave()!;
        rollout.MarkWaveDispatched(second.Id, Now);

        var inSecond = second.Targets.Select(target => target.StoreId).ToArray();
        Assert.False(rollout.RecordResult(inSecond[0], false, "one", Now));
        Assert.False(rollout.RecordResult(inSecond[1], false, "two", Now));
        Assert.Equal(RolloutState.InProgress, rollout.State);

        // Finish the wave so the rollout can move on, then fail a third store in
        // the last wave: the count spans waves.
        foreach (var storeId in inSecond.Skip(2))
        {
            rollout.RecordResult(storeId, succeeded: true, null, Now);
        }

        var third = rollout.NextWave()!;
        rollout.MarkWaveDispatched(third.Id, Now);

        var halted = rollout.RecordResult(third.Targets[0].StoreId, false, "three", Now);

        Assert.True(halted);
        Assert.Equal(RolloutState.Halted, rollout.State);
        Assert.Equal(3, rollout.FailedStores);
    }

    [Fact]
    public void AFailedCanaryHaltsEvenWhenTheThresholdWouldTolerateFailures()
    {
        // The threshold says how many failures are tolerable across a fleet. It
        // is not permission to carry on past the one store that exists precisely
        // to be broken first.
        var rollout = Plan(30, [50, 100], threshold: 10);
        rollout.Start(Now);

        var canary = rollout.NextWave()!;
        rollout.MarkWaveDispatched(canary.Id, Now);

        var halted = rollout.RecordResult(canary.Targets[0].StoreId, succeeded: false, "boom", Now);

        Assert.True(halted);
        Assert.Equal(RolloutState.Halted, rollout.State);
        Assert.Contains("canary", rollout.HaltReason!, StringComparison.OrdinalIgnoreCase);
        Assert.Null(rollout.NextWave());
    }

    [Fact]
    public void ACleanRunCompletesTheRollout()
    {
        var rollout = Plan(4, [100]);
        rollout.Start(Now);

        while (rollout.NextWave() is { } wave)
        {
            rollout.MarkWaveDispatched(wave.Id, Now);

            foreach (var target in wave.Targets)
            {
                rollout.RecordResult(target.StoreId, succeeded: true, null, Now);
            }
        }

        Assert.Equal(RolloutState.Completed, rollout.State);
        Assert.Equal(4, rollout.SucceededStores);
        Assert.Equal(0, rollout.FailedStores);
        Assert.NotNull(rollout.CompletedAt);
    }

    [Fact]
    public void ResumingAcceptsTheFailuresSoFarRatherThanForgettingThem()
    {
        var rollout = Plan(12, [50, 100], threshold: 1);
        rollout.Start(Now);

        var canary = rollout.NextWave()!;
        rollout.MarkWaveDispatched(canary.Id, Now);
        rollout.RecordResult(canary.Targets[0].StoreId, succeeded: true, null, Now);

        var second = rollout.NextWave()!;
        rollout.MarkWaveDispatched(second.Id, Now);
        rollout.RecordResult(second.Targets[0].StoreId, false, "known flake", Now);

        Assert.Equal(RolloutState.Halted, rollout.State);

        rollout.Resume(Now);

        Assert.Equal(RolloutState.InProgress, rollout.State);
        Assert.Null(rollout.HaltReason);

        // The failure is still counted, and the very next one halts it again.
        Assert.Equal(1, rollout.FailedStores);

        var halted = rollout.RecordResult(second.Targets[1].StoreId, false, "not a flake after all", Now);

        Assert.True(halted);
        Assert.Equal(RolloutState.Halted, rollout.State);
    }

    [Fact]
    public void CancellingLeavesAlreadyUpgradedStoresAlone()
    {
        // A rollout is not a transaction. Automatically downgrading production
        // stores that are working would be a worse outage than the one being
        // avoided.
        var rollout = Plan(6);
        rollout.Start(Now);

        var canary = rollout.NextWave()!;
        rollout.MarkWaveDispatched(canary.Id, Now);
        rollout.RecordResult(canary.Targets[0].StoreId, succeeded: true, null, Now);

        rollout.Cancel("superseded by 1.2.0", Now);

        Assert.Equal(RolloutState.Cancelled, rollout.State);
        Assert.Equal(1, rollout.SucceededStores);
        Assert.Null(rollout.NextWave());
    }

    [Fact]
    public void ACompletedRolloutCannotBeCancelledOrRestarted()
    {
        var rollout = Plan(1);
        rollout.Start(Now);

        var canary = rollout.NextWave()!;
        rollout.MarkWaveDispatched(canary.Id, Now);
        rollout.RecordResult(canary.Targets[0].StoreId, succeeded: true, null, Now);

        Assert.Equal(RolloutState.Completed, rollout.State);
        Assert.Throws<DomainException>(() => rollout.Cancel("too late", Now));
        Assert.Throws<DomainException>(() => rollout.Start(Now));
    }

    [Fact]
    public void ResultsForStoresOutsideTheRolloutAreRefused()
    {
        var rollout = Plan(3);
        rollout.Start(Now);

        Assert.Throws<DomainException>(() =>
            rollout.RecordResult(Guid.NewGuid(), succeeded: true, null, Now));
    }

    [Fact]
    public void TheCanaryStillGoesFirstWhenTheWavesComeBackInAnotherOrder()
    {
        // The database returns a rollout's waves in whatever order the query
        // produced, which is neither insertion order nor stable. A freshly
        // planned rollout is in order by accident, so this defect is invisible
        // in memory and appeared only after a reload: the aggregate dispatched
        // wave 1 while the canary was still pending, which defeats the whole
        // point of a staged rollout.
        //
        // Reversing the backing collection is the cheapest faithful stand-in for
        // that reload.
        var rollout = Plan(8, [50, 100]);

        var backing = typeof(FeatureRollout)
            .GetField("_waves", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .GetValue(rollout) as List<RolloutWave>;

        backing!.Reverse();

        rollout.Start(Now);

        var first = rollout.NextWave() ?? rollout.CurrentWave();

        Assert.NotNull(first);
        Assert.True(first!.IsCanary, "The canary must be the first wave dispatched, whatever order the waves arrived in.");
        Assert.Equal(0, first.Ordinal);

        // And the ordered view is what callers see, so a dashboard cannot render
        // the waves out of sequence either.
        Assert.Equal(
            Enumerable.Range(0, rollout.Waves.Count).ToArray(),
            rollout.Waves.Select(wave => wave.Ordinal).ToArray());
    }

    [Fact]
    public void AWaveCannotBeDispatchedTwice()
    {
        var rollout = Plan(4);
        rollout.Start(Now);

        var canary = rollout.NextWave()!;
        rollout.MarkWaveDispatched(canary.Id, Now);

        Assert.Throws<DomainException>(() => rollout.MarkWaveDispatched(canary.Id, Now));
    }
}
