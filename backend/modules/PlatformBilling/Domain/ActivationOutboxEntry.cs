using Knight.Domain.Common;
using Knight.Domain.Exceptions;

namespace PlatformBilling.Domain;

/// <summary>
/// The transactional-outbox record for "a paid subscription was activated; provision
/// its store" (hardening backlog P2).
///
/// The webhook writes one of these in the <b>same</b> unit of work as the activation
/// itself, so the two commit together. A background dispatcher then hands it to
/// provisioning and marks it done. That is what closes the one window the in-process
/// handoff could not: a crash after the activation commits but before the store is
/// created no longer leaves a paid subscription with no store — the outbox row
/// survived the crash, and the dispatcher will act on it.
///
/// Delivery is at-least-once, and the provisioning listener is idempotent (it reuses
/// an existing store), so a row dispatched twice provisions once.
/// </summary>
public sealed class ActivationOutboxEntry : AuditableEntity, ICustomerOwned
{
    public Guid CustomerId { get; private set; }

    public Guid SubscriptionId { get; private set; }

    public Guid PlanId { get; private set; }

    public ActivationOutboxStatus Status { get; private set; }

    public int AttemptCount { get; private set; }

    /// <summary>The earliest a failed attempt may be retried — backoff, not a busy loop.</summary>
    public DateTimeOffset NextAttemptAt { get; private set; }

    public DateTimeOffset? DispatchedAt { get; private set; }

    public string? LastError { get; private set; }

    private ActivationOutboxEntry()
    {
    }

    private ActivationOutboxEntry(Guid id, DateTimeOffset createdAt, Guid customerId, Guid subscriptionId, Guid planId)
        : base(id, createdAt)
    {
        CustomerId = customerId;
        SubscriptionId = subscriptionId;
        PlanId = planId;
        Status = ActivationOutboxStatus.Pending;
        NextAttemptAt = createdAt;
    }

    public static ActivationOutboxEntry Queue(Guid id, DateTimeOffset createdAt, Guid customerId, Guid subscriptionId, Guid planId)
    {
        if (customerId == Guid.Empty || subscriptionId == Guid.Empty)
        {
            throw DomainException.Validation("An activation outbox entry must name a customer and a subscription.");
        }

        return new ActivationOutboxEntry(id, createdAt, customerId, subscriptionId, planId);
    }

    public bool CanAttemptAt(DateTimeOffset now) => Status is ActivationOutboxStatus.Pending && now >= NextAttemptAt;

    public void MarkDispatched(DateTimeOffset now)
    {
        Status = ActivationOutboxStatus.Dispatched;
        DispatchedAt = now;
        LastError = null;
        MarkUpdated(now);
    }

    /// <summary>
    /// Records a failed attempt. Under the attempt ceiling it schedules a backed-off
    /// retry; at the ceiling it dead-letters, so a permanently failing handoff stops
    /// spinning and becomes something a person can see.
    /// </summary>
    public void MarkFailed(string error, DateTimeOffset now, int maxAttempts)
    {
        AttemptCount++;
        LastError = string.IsNullOrWhiteSpace(error) ? "Unknown error." : error.Trim();

        if (AttemptCount >= maxAttempts)
        {
            Status = ActivationOutboxStatus.DeadLettered;
        }
        else
        {
            // 30s, 60s, 120s, 240s… capped, and jitter-free because one dispatcher
            // owns the sweep — there is nothing to thunder against.
            var backoffSeconds = Math.Min(30 * Math.Pow(2, AttemptCount - 1), 900);
            NextAttemptAt = now.AddSeconds(backoffSeconds);
        }

        MarkUpdated(now);
    }
}

public enum ActivationOutboxStatus
{
    Pending = 0,
    Dispatched = 1,
    DeadLettered = 2,
}
