using Knight.Application.Abstractions.ControlPlane;
using Knight.Application.Abstractions.Time;
using Microsoft.Extensions.Logging;
using PlatformBilling.Domain;
using PlatformBilling.Payments;
using Subscriptions;
using Subscriptions.Domain;

namespace PlatformBilling;

/// <summary>
/// Settles provider webhooks. See <see cref="IPlatformWebhookService"/> for the
/// promises; the mechanics:
///
/// A webhook carries no customer scope — it is authenticated by the provider's
/// signature, not by a KNIGHT session — so this runs in platform scope
/// deliberately, and only after the signature verifies. Idempotency is anchored
/// on the pending transaction the checkout wrote: a redelivered event finds a
/// transaction that is already <see cref="PlatformBillingTransactionStatus.Succeeded"/>
/// and returns without charging, activating or provisioning a second time.
/// </summary>
internal sealed class PlatformWebhookService : IPlatformWebhookService
{
    private readonly IPlatformPaymentProviderRegistry _providers;
    private readonly ICheckoutSessionRepository _sessions;
    private readonly IPlatformBillingTransactionRepository _transactions;
    private readonly ISubscriptionRepository _subscriptions;
    private readonly IEntitlementService _entitlements;
    private readonly IEnumerable<ISubscriptionActivatedListener> _listeners;
    private readonly ICustomerScopeAccessor _scope;
    private readonly IAuditTrail _audit;
    private readonly IDateTimeProvider _clock;
    private readonly ILogger<PlatformWebhookService> _logger;

    public PlatformWebhookService(
        IPlatformPaymentProviderRegistry providers,
        ICheckoutSessionRepository sessions,
        IPlatformBillingTransactionRepository transactions,
        ISubscriptionRepository subscriptions,
        IEntitlementService entitlements,
        IEnumerable<ISubscriptionActivatedListener> listeners,
        ICustomerScopeAccessor scope,
        IAuditTrail audit,
        IDateTimeProvider clock,
        ILogger<PlatformWebhookService> logger)
    {
        _providers = providers;
        _sessions = sessions;
        _transactions = transactions;
        _subscriptions = subscriptions;
        _entitlements = entitlements;
        _listeners = listeners;
        _scope = scope;
        _audit = audit;
        _clock = clock;
        _logger = logger;
    }

    public async Task<WebhookResult> HandleAsync(string provider, string payload, string? signature, CancellationToken cancellationToken)
    {
        if (!_providers.TryResolve(provider, out var gateway))
        {
            return new WebhookResult(WebhookOutcome.UnknownSession);
        }

        if (!gateway.VerifySignature(payload, signature))
        {
            _logger.LogWarning("A {Provider} webhook failed signature verification.", gateway.Name);
            return new WebhookResult(WebhookOutcome.InvalidSignature);
        }

        if (!gateway.TryParseEvent(payload, out var paymentEvent))
        {
            return new WebhookResult(WebhookOutcome.Malformed);
        }

        if (paymentEvent.Kind is PlatformPaymentEventKind.Unhandled)
        {
            return new WebhookResult(WebhookOutcome.Ignored);
        }

        // The callback has no session of its own, so it acts platform-wide — but
        // only now that its signature has proven it authentic.
        _scope.SetPlatformScope();

        var session = await _sessions.FindByProviderSessionAsync(gateway.Name, paymentEvent.ProviderSessionId, cancellationToken);
        if (session is null)
        {
            _logger.LogWarning(
                "A {Provider} webhook named checkout session {SessionId}, which does not exist.",
                gateway.Name,
                paymentEvent.ProviderSessionId);
            return new WebhookResult(WebhookOutcome.UnknownSession);
        }

        var transaction = await _transactions.FindByIdempotencyKeyAsync(
            CheckoutService.IdempotencyKeyFor(session.Id),
            cancellationToken);

        if (transaction is null)
        {
            // A session with no transaction is a data error, not a client one.
            _logger.LogError("Checkout session {SessionId} has no billing transaction to settle.", session.Id);
            return new WebhookResult(WebhookOutcome.UnknownSession);
        }

        // The idempotency gate. A charge already settled is not settled again,
        // whatever the provider redelivers.
        if (transaction.Status is not PlatformBillingTransactionStatus.Pending)
        {
            return new WebhookResult(WebhookOutcome.AlreadyProcessed, session.SubscriptionId);
        }

        return paymentEvent.Kind switch
        {
            PlatformPaymentEventKind.PaymentSucceeded =>
                await ActivateAsync(gateway.Name, session, transaction, paymentEvent, cancellationToken),
            PlatformPaymentEventKind.PaymentFailed =>
                await FailAsync(session, transaction, cancellationToken),
            _ => new WebhookResult(WebhookOutcome.Ignored),
        };
    }

    private async Task<WebhookResult> ActivateAsync(
        string providerName,
        CheckoutSession session,
        PlatformBillingTransaction transaction,
        ProviderPaymentEvent paymentEvent,
        CancellationToken cancellationToken)
    {
        var subscription = await _subscriptions.GetByIdAsync(session.SubscriptionId, cancellationToken);
        if (subscription is null)
        {
            _logger.LogError("Checkout session {SessionId} names subscription {SubscriptionId}, which is gone.", session.Id, session.SubscriptionId);
            return new WebhookResult(WebhookOutcome.UnknownSession);
        }

        var now = _clock.UtcNow;

        transaction.AttachProviderTransaction(paymentEvent.ProviderTransactionId, now);
        transaction.Succeed(now);

        // Already-active is not an error: two deliveries that raced past the
        // idempotency gate must both be safe. Only a pending or lapsed
        // subscription is activated.
        if (subscription.Status is SubscriptionStatus.Pending or SubscriptionStatus.PastDue or SubscriptionStatus.Suspended)
        {
            subscription.Activate(now);
        }

        session.Complete(now);

        await _subscriptions.SaveChangesAsync(cancellationToken);

        // Resolve the entitlements the now-active subscription owes: the plan's
        // included features plus the ones the customer chose at checkout. This is
        // the desired-state the delivery engine consumes in phase E.
        await _entitlements.ReconcileAsync(subscription.CustomerId, cancellationToken);

        await _audit.RecordAsync(
            "billing.subscription.activated",
            nameof(Subscription),
            subscription.Id.ToString(),
            subscription.CustomerId,
            cancellationToken,
            newValue: new { providerName, transaction.ProviderTransactionId, transaction.Amount, transaction.Currency });

        // Provisioning is wired here in phase D. A listener runs after the
        // activation is committed, so nothing it does can un-take the payment, and
        // it must be idempotent because a redelivered webhook can reach it again.
        var context = new SubscriptionActivatedContext(subscription.CustomerId, subscription.Id, subscription.PlanId);
        foreach (var listener in _listeners)
        {
            try
            {
                await listener.OnActivatedAsync(context, cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // A provisioning failure is recovered on its own machinery
                // (retry/coordinator); it must not turn a settled payment into a
                // failed webhook the provider keeps redelivering.
                _logger.LogError(
                    exception,
                    "A post-activation listener failed for subscription {SubscriptionId}; the payment stands and provisioning will be retried.",
                    subscription.Id);
            }
        }

        return new WebhookResult(WebhookOutcome.Processed, subscription.Id);
    }

    private async Task<WebhookResult> FailAsync(
        CheckoutSession session,
        PlatformBillingTransaction transaction,
        CancellationToken cancellationToken)
    {
        var now = _clock.UtcNow;
        transaction.Fail(now);
        session.Cancel(now);
        await _subscriptions.SaveChangesAsync(cancellationToken);

        await _audit.RecordAsync(
            "billing.payment.failed",
            nameof(PlatformBillingTransaction),
            transaction.Id.ToString(),
            transaction.CustomerId,
            cancellationToken,
            newValue: new { session.SubscriptionId });

        return new WebhookResult(WebhookOutcome.Processed, session.SubscriptionId);
    }
}
