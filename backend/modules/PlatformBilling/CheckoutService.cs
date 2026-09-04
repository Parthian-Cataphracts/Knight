using FeatureRegistry.Domain;
using Knight.Application.Abstractions.ControlPlane;
using Knight.Application.Abstractions.Time;
using Knight.Domain.Common;
using Microsoft.Extensions.Options;
using Plans;
using Plans.Domain;
using PlatformBilling.Domain;
using PlatformBilling.Payments;
using Subscriptions.Domain;

namespace PlatformBilling;

/// <summary>
/// The self-service checkout. See <see cref="ICheckoutService"/> for the contract;
/// the invariants that keep it honest live here:
///
/// <list type="bullet">
/// <item>Only a plan that is both live and publicly purchasable can be bought.</item>
/// <item>A selected feature must be one the plan offers the customer to choose;
/// anything else is refused rather than silently priced.</item>
/// <item>The price is recomputed from the plan and the feature prices in force —
/// a client-supplied amount is never read.</item>
/// <item>The subscription is created <see cref="SubscriptionStatus.Pending"/>; it
/// entitles nothing until a verified payment activates it.</item>
/// </list>
/// </summary>
internal sealed class CheckoutService : ICheckoutService
{
    private readonly IPlanRepository _plans;
    private readonly IFeatureRepository _features;
    private readonly IPricingCalculator _pricing;
    private readonly ISubscriptionRepository _subscriptions;
    private readonly ICheckoutSessionRepository _sessions;
    private readonly IPlatformBillingTransactionRepository _transactions;
    private readonly IPlatformPaymentProviderRegistry _providers;
    private readonly IAuditTrail _audit;
    private readonly IDateTimeProvider _clock;
    private readonly PlatformBillingOptions _options;

    public CheckoutService(
        IPlanRepository plans,
        IFeatureRepository features,
        IPricingCalculator pricing,
        ISubscriptionRepository subscriptions,
        ICheckoutSessionRepository sessions,
        IPlatformBillingTransactionRepository transactions,
        IPlatformPaymentProviderRegistry providers,
        IAuditTrail audit,
        IDateTimeProvider clock,
        IOptions<PlatformBillingOptions> options)
    {
        _plans = plans;
        _features = features;
        _pricing = pricing;
        _subscriptions = subscriptions;
        _sessions = sessions;
        _transactions = transactions;
        _providers = providers;
        _audit = audit;
        _clock = clock;
        _options = options.Value;
    }

    public async Task<CheckoutResult> CheckoutAsync(CheckoutRequest request, CancellationToken cancellationToken)
    {
        var providerName = string.IsNullOrWhiteSpace(request.Provider) ? _options.DefaultProvider : request.Provider.Trim();
        if (!_providers.TryResolve(providerName, out var provider))
        {
            throw SelfServiceBillingException.PlanUnavailable($"No payment provider named '{providerName}' is available.");
        }

        var plan = await _plans.GetByIdAsync(request.PlanId, cancellationToken)
            ?? throw SelfServiceBillingException.PlanUnavailable("That plan is not available.");

        if (!plan.IsActive || !plan.IsPubliclyPurchasable)
        {
            throw SelfServiceBillingException.PlanUnavailable($"The '{plan.Name}' plan is not on public sale.");
        }

        var selected = (request.SelectedFeatureIds ?? []).Distinct().ToArray();
        await ValidateSelectionAsync(plan, selected, cancellationToken);

        var now = _clock.UtcNow;

        // The price the provider will collect, computed here from the plan and the
        // prices in force — never from anything the client sent. QuoteAsync itself
        // refuses a selected feature that has no price rather than quoting it free.
        var quote = await _pricing.QuoteAsync(new QuoteRequest(plan, selected, now), cancellationToken);

        var periodEnd = request.Interval switch
        {
            BillingInterval.Yearly => now.AddYears(1),
            _ => now.AddMonths(1),
        };

        var subscription = Subscription.StartPending(
            Guid.CreateVersion7(),
            now,
            request.CustomerId,
            plan.Id,
            now,
            periodEnd);

        foreach (var featureId in selected)
        {
            _subscriptions.RegisterNewFeature(subscription.EnableFeature(featureId, enabledBy: null, now));
        }

        subscription.LinkProvider(provider.Name, $"sub_{subscription.Id:n}", now);
        await _subscriptions.AddAsync(subscription, cancellationToken);

        var session = CheckoutSession.Open(
            Guid.CreateVersion7(),
            now,
            request.CustomerId,
            plan.Id,
            subscription.Id,
            request.Interval,
            selected,
            quote.Subtotal,
            now.Add(_options.SessionLifetime));
        await _sessions.AddAsync(session, cancellationToken);

        // The pending charge the webhook settles. Its idempotency key is the
        // session id, which is exactly what the webhook can recompute from the
        // session it finds — so a replayed webhook lands on this one row.
        var transaction = PlatformBillingTransaction.Record(
            Guid.CreateVersion7(),
            now,
            request.CustomerId,
            subscription.Id,
            provider.Name,
            quote.Subtotal,
            IdempotencyKeyFor(session.Id));
        await _transactions.AddAsync(transaction, cancellationToken);

        var start = await provider.StartCheckoutAsync(session, cancellationToken);
        session.AttachProviderSession(provider.Name, start.ProviderSessionId, now);

        // One unit of work: subscription, its features, the session and the
        // transaction are inserted together, so a checkout can never be observed
        // half-made.
        await _subscriptions.SaveChangesAsync(cancellationToken);

        await _audit.RecordAsync(
            "billing.checkout.opened",
            nameof(CheckoutSession),
            session.Id.ToString(),
            request.CustomerId,
            cancellationToken,
            newValue: new { session.PlanId, subscription.Id, quote.Subtotal.Amount, quote.Subtotal.Currency, provider.Name });

        return new CheckoutResult(session.Id, subscription.Id, start.CheckoutUrl, quote.Subtotal.Amount, quote.Subtotal.Currency);
    }

    /// <summary>The idempotency key a session's charge is recorded under.</summary>
    public static string IdempotencyKeyFor(Guid checkoutSessionId) => $"checkout:{checkoutSessionId:n}";

    private async Task ValidateSelectionAsync(Plan plan, IReadOnlyCollection<Guid> selected, CancellationToken cancellationToken)
    {
        if (selected.Count == 0)
        {
            return;
        }

        var selectable = plan.SelectableFeatureIds.ToHashSet();

        foreach (var featureId in selected)
        {
            // A feature the plan does not offer the customer to choose — one it
            // includes outright, or one it does not list at all — is not a
            // self-service purchase decision to make at checkout.
            if (!selectable.Contains(featureId))
            {
                throw SelfServiceBillingException.InvalidFeatureSelection(
                    $"Feature '{featureId}' is not offered as an optional add-on on the '{plan.Name}' plan.");
            }

            // A draft or withdrawn feature cannot be entitled, so it cannot be
            // sold either, whatever the plan still lists.
            var feature = await _features.GetByIdAsync(featureId, cancellationToken);
            if (feature is null || !feature.CanBeEntitled)
            {
                throw SelfServiceBillingException.InvalidFeatureSelection(
                    $"Feature '{featureId}' is not currently available to purchase.");
            }
        }
    }
}
