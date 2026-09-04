using PlatformBilling.Domain;

namespace PlatformBilling;

/// <summary>What a merchant asked to buy. The price is never among the inputs.</summary>
public sealed record CheckoutRequest(
    Guid CustomerId,
    Guid PlanId,
    BillingInterval Interval,
    IReadOnlyCollection<Guid> SelectedFeatureIds,
    string? Provider);

/// <summary>
/// The outcome of opening a checkout: where to send the browser and the ids the
/// caller can watch. The amount is echoed back for display only — it was computed
/// server-side and is what the provider will collect, never a figure the client
/// can influence.
/// </summary>
public sealed record CheckoutResult(
    Guid CheckoutSessionId,
    Guid SubscriptionId,
    string CheckoutUrl,
    decimal Amount,
    string Currency);

/// <summary>
/// Opens a self-service checkout: validates the plan is on public sale and the
/// chosen features are ones the plan offers, computes the authoritative price,
/// creates the <see cref="SubscriptionStatus.Pending"/> subscription and the
/// <see cref="CheckoutSession"/> and <see cref="PlatformBillingTransaction"/> that
/// a verified webhook later settles (docs/self-service-saas-plan.md §6, §7).
///
/// Nothing here grants anything: a pending subscription entitles the customer to
/// nothing until <see cref="IPlatformWebhookService"/> activates it on a confirmed
/// payment.
/// </summary>
public interface ICheckoutService
{
    Task<CheckoutResult> CheckoutAsync(CheckoutRequest request, CancellationToken cancellationToken);
}
