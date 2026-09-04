using Knight.Application.Abstractions.Time;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using PlatformBilling;
using PlatformBilling.Domain;
using Xunit;

namespace Knight.UnitTests.ControlPlane;

/// <summary>
/// The activation outbox — the durable payment → provisioning handoff (hardening
/// backlog P2). The entry's own state machine, and the dispatcher that drains it.
/// </summary>
public sealed class ActivationOutboxTests
{
    private static readonly DateTimeOffset Now = new(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);

    private static ActivationOutboxEntry Queue() =>
        ActivationOutboxEntry.Queue(Guid.CreateVersion7(), Now, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());

    [Fact]
    public void ANewEntryIsAttemptableNow()
    {
        var entry = Queue();

        Assert.Equal(ActivationOutboxStatus.Pending, entry.Status);
        Assert.True(entry.CanAttemptAt(Now));
    }

    [Fact]
    public void AFailedEntryBacksOffAndIsNotAttemptableUntilLater()
    {
        var entry = Queue();

        entry.MarkFailed("provisioning is down", Now, maxAttempts: 8);

        Assert.Equal(ActivationOutboxStatus.Pending, entry.Status);
        Assert.Equal(1, entry.AttemptCount);
        Assert.False(entry.CanAttemptAt(Now));
        Assert.True(entry.CanAttemptAt(Now.AddMinutes(30)));
    }

    [Fact]
    public void AnEntryDeadLettersAtTheAttemptCeiling()
    {
        var entry = Queue();

        for (var i = 0; i < 8; i++)
        {
            entry.MarkFailed("still down", Now, maxAttempts: 8);
        }

        Assert.Equal(ActivationOutboxStatus.DeadLettered, entry.Status);
        Assert.False(entry.CanAttemptAt(Now.AddDays(1)));
    }

    [Fact]
    public async Task TheDispatcherHandsADueEntryToProvisioningAndMarksItDispatched()
    {
        var entry = Queue();
        var repo = Substitute.For<IActivationOutboxRepository>();
        repo.ListDispatchableAsync(Arg.Any<int>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns([entry]);

        var listener = Substitute.For<ISubscriptionActivatedListener>();
        var clock = Substitute.For<IDateTimeProvider>();
        clock.UtcNow.Returns(Now);

        var dispatcher = new ActivationOutboxDispatcher(repo, [listener], clock, NullLogger<ActivationOutboxDispatcher>.Instance);

        var dispatched = await dispatcher.DispatchDueAsync(50, CancellationToken.None);

        Assert.Equal(1, dispatched);
        Assert.Equal(ActivationOutboxStatus.Dispatched, entry.Status);
        await listener.Received(1).OnActivatedAsync(Arg.Any<SubscriptionActivatedContext>(), Arg.Any<CancellationToken>());
        await repo.Received().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AFailingListenerLeavesTheEntryForRetryRatherThanLosingIt()
    {
        var entry = Queue();
        var repo = Substitute.For<IActivationOutboxRepository>();
        repo.ListDispatchableAsync(Arg.Any<int>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns([entry]);

        var listener = Substitute.For<ISubscriptionActivatedListener>();
        listener.OnActivatedAsync(Arg.Any<SubscriptionActivatedContext>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new InvalidOperationException("provisioning is down")));

        var clock = Substitute.For<IDateTimeProvider>();
        clock.UtcNow.Returns(Now);

        var dispatcher = new ActivationOutboxDispatcher(repo, [listener], clock, NullLogger<ActivationOutboxDispatcher>.Instance);

        var dispatched = await dispatcher.DispatchDueAsync(50, CancellationToken.None);

        Assert.Equal(0, dispatched);
        Assert.Equal(ActivationOutboxStatus.Pending, entry.Status); // still there, to be retried
        Assert.Equal(1, entry.AttemptCount);
        await repo.Received().SaveChangesAsync(Arg.Any<CancellationToken>());
    }
}
