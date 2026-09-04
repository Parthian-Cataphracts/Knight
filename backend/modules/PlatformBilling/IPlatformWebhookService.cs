namespace PlatformBilling;

public enum WebhookOutcome
{
    /// <summary>The event was applied: a subscription activated, or a charge recorded failed.</summary>
    Processed = 0,

    /// <summary>A replay of an event already applied. Nothing changed; the prior result stands.</summary>
    AlreadyProcessed = 1,

    /// <summary>A well-formed event KNIGHT does not act on. Acknowledged, not retried.</summary>
    Ignored = 2,

    /// <summary>The signature did not verify. The body was never parsed.</summary>
    InvalidSignature = 3,

    /// <summary>The body was not a payload this provider recognises.</summary>
    Malformed = 4,

    /// <summary>The event named a checkout KNIGHT has no record of.</summary>
    UnknownSession = 5,
}

public sealed record WebhookResult(WebhookOutcome Outcome, Guid? SubscriptionId = null);

/// <summary>
/// The one and only path that activates a paid subscription
/// (docs/self-service-saas-plan.md §7). A browser landing on a success page never
/// activates anything; only a signature-verified, idempotent, replay-resistant
/// provider webhook does. After a successful activation it runs any registered
/// <see cref="ISubscriptionActivatedListener"/> — which is where provisioning is
/// wired in phase D — but the activation itself is committed first, so a listener
/// that fails cannot un-take a payment.
/// </summary>
public interface IPlatformWebhookService
{
    Task<WebhookResult> HandleAsync(string provider, string payload, string? signature, CancellationToken cancellationToken);
}

/// <summary>
/// Notified once, after a subscription has been activated and its entitlements
/// resolved, in the same platform-scoped context. Phase D's provisioning wire is
/// the first implementation: it creates the store record and starts the
/// provisioning job. Listeners run after the activation is committed and must be
/// idempotent — a redelivered webhook can call them again.
/// </summary>
public interface ISubscriptionActivatedListener
{
    Task OnActivatedAsync(SubscriptionActivatedContext context, CancellationToken cancellationToken);
}

public sealed record SubscriptionActivatedContext(Guid CustomerId, Guid SubscriptionId, Guid PlanId);
