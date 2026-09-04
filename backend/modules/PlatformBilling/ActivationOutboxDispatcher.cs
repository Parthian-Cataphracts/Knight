using Knight.Application.Abstractions.Time;
using Microsoft.Extensions.Logging;
using PlatformBilling.Domain;

namespace PlatformBilling;

/// <summary>
/// Drains the activation outbox: reads the "provision this store" intents the
/// webhook committed and hands each to the post-activation listeners
/// (provisioning), marking it dispatched — or, on failure, scheduling a backed-off
/// retry and dead-lettering after too many (hardening backlog P2).
///
/// At-least-once by construction, which is safe because the provisioning listener
/// is idempotent: a redispatched entry reuses the store it already created.
///
/// It assumes a single sweeper — KNIGHT deploys as one instance — so it does not
/// claim rows. Running it on several replicas at once would let two sweeps grab
/// the same entry and both start one store's provisioning, which the store's
/// idempotency key turns into a duplicate-insert rather than a second store;
/// making that safe (a <c>FOR UPDATE SKIP LOCKED</c> claim) is the change to make
/// before scaling the control plane horizontally.
/// </summary>
public interface IActivationOutboxDispatcher
{
    Task<int> DispatchDueAsync(int limit, CancellationToken cancellationToken);
}

internal sealed class ActivationOutboxDispatcher : IActivationOutboxDispatcher
{
    /// <summary>Attempts before an entry dead-letters and stops being retried.</summary>
    private const int MaxAttempts = 8;

    private readonly IActivationOutboxRepository _outbox;
    private readonly IEnumerable<ISubscriptionActivatedListener> _listeners;
    private readonly IDateTimeProvider _clock;
    private readonly ILogger<ActivationOutboxDispatcher> _logger;

    public ActivationOutboxDispatcher(
        IActivationOutboxRepository outbox,
        IEnumerable<ISubscriptionActivatedListener> listeners,
        IDateTimeProvider clock,
        ILogger<ActivationOutboxDispatcher> logger)
    {
        _outbox = outbox;
        _listeners = listeners;
        _clock = clock;
        _logger = logger;
    }

    public async Task<int> DispatchDueAsync(int limit, CancellationToken cancellationToken)
    {
        var due = await _outbox.ListDispatchableAsync(limit, _clock.UtcNow, cancellationToken);
        var dispatched = 0;

        foreach (var entry in due)
        {
            var context = new SubscriptionActivatedContext(entry.CustomerId, entry.SubscriptionId, entry.PlanId);

            try
            {
                foreach (var listener in _listeners)
                {
                    await listener.OnActivatedAsync(context, cancellationToken);
                }

                entry.MarkDispatched(_clock.UtcNow);
                dispatched++;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                entry.MarkFailed(exception.Message, _clock.UtcNow, MaxAttempts);

                _logger.LogError(
                    exception,
                    "Activation outbox entry {EntryId} for subscription {SubscriptionId} failed (attempt {Attempt}); {Disposition}.",
                    entry.Id,
                    entry.SubscriptionId,
                    entry.AttemptCount,
                    entry.Status is ActivationOutboxStatus.DeadLettered ? "dead-lettered" : "will retry");
            }

            // One row at a time: a failure on one must not roll back another that
            // dispatched cleanly in the same sweep.
            await _outbox.SaveChangesAsync(cancellationToken);
        }

        return dispatched;
    }
}
