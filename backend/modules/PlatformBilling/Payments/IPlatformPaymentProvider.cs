using PlatformBilling.Domain;

namespace PlatformBilling.Payments;

/// <summary>
/// The result of asking a provider to open a hosted checkout: where to send the
/// merchant's browser, and the id the provider will name this checkout by in the
/// webhook that later confirms it.
/// </summary>
public sealed record CheckoutStart(string CheckoutUrl, string ProviderSessionId);

/// <summary>What a parsed, verified provider webhook says happened.</summary>
public enum PlatformPaymentEventKind
{
    /// <summary>Not a kind KNIGHT acts on — acknowledged and ignored.</summary>
    Unhandled = 0,
    PaymentSucceeded = 1,
    PaymentFailed = 2,
}

/// <summary>
/// A payment event a provider delivered, already tied back to the checkout it
/// belongs to. <see cref="ProviderSessionId"/> is how the webhook finds the
/// <see cref="CheckoutSession"/>; <see cref="ProviderTransactionId"/> is the
/// charge's own id, recorded on the <see cref="PlatformBillingTransaction"/> so a
/// refund can be traced later.
/// </summary>
public sealed record ProviderPaymentEvent(
    PlatformPaymentEventKind Kind,
    string ProviderSessionId,
    string ProviderTransactionId);

/// <summary>
/// KNIGHT's own payment provider abstraction (merchant → KNIGHT), never a store's
/// gateway (docs/self-service-saas-plan.md §3). A real provider — the one the
/// product owner has yet to choose (§11) — implements this; the
/// <see cref="SimulatedPaymentProvider"/> stands in until then so the whole
/// journey and its acceptance test run locally.
///
/// The contract is deliberately narrow: open a checkout, verify a webhook's
/// signature, and parse a verified webhook into an event. Everything about what
/// activation <i>means</i> — the transaction, the subscription, the entitlements
/// — stays in <see cref="IPlatformWebhookService"/>, so a second provider is a
/// new adapter and nothing else.
/// </summary>
public interface IPlatformPaymentProvider
{
    /// <summary>The provider key used in the webhook route and stored on every row it writes.</summary>
    string Name { get; }

    Task<CheckoutStart> StartCheckoutAsync(CheckoutSession session, CancellationToken cancellationToken);

    /// <summary>
    /// Whether a webhook body is authentic. A provider that signs its callbacks
    /// checks the signature here; a request that fails is never parsed, so an
    /// unauthenticated caller can neither settle a charge nor probe for one.
    /// </summary>
    bool VerifySignature(string payload, string? signature);

    /// <summary>
    /// Reads a <b>verified</b> payload into an event. Returns false for a body
    /// this provider does not recognise rather than throwing, so an unknown event
    /// type is acknowledged and ignored rather than retried forever.
    /// </summary>
    bool TryParseEvent(string payload, out ProviderPaymentEvent paymentEvent);
}

/// <summary>Resolves the provider a webhook route names, or refuses an unknown one.</summary>
public interface IPlatformPaymentProviderRegistry
{
    IPlatformPaymentProvider Resolve(string name);

    bool TryResolve(string name, out IPlatformPaymentProvider provider);
}
