using Subscriptions.Domain;

namespace Subscriptions;

public sealed record StartSubscriptionInput(
    Guid CustomerId,
    Guid PlanId,
    IReadOnlyCollection<Guid> FeatureIds,
    bool AsTrial);

public sealed record SubscriptionListQuery(int Page, int PageSize, Guid? CustomerId, SubscriptionStatus? Status);

public sealed record SubscriptionPage(IReadOnlyCollection<Subscription> Items, int Page, int PageSize, long TotalCount);

public sealed record QuoteLineView(string Description, Guid? FeatureId, decimal UnitPrice, int Quantity, decimal Total);

public sealed record QuoteView(string Currency, decimal Subtotal, IReadOnlyCollection<QuoteLineView> Lines);

/// <summary>
/// Subscription lifecycle and the entitlement changes each transition implies.
///
/// Every path through here ends in reconciliation, so entitlements are never left
/// describing a subscription that has since changed. The rules the plan imposes —
/// what a customer may switch on, and what needs infrastructure they do not have —
/// are enforced here rather than in the aggregate, because answering them requires
/// reading the plan and the customer's stores.
/// </summary>
public interface ISubscriptionService
{
    Task<Subscription> StartAsync(StartSubscriptionInput input, CancellationToken cancellationToken);

    Task<Subscription?> GetAsync(Guid id, CancellationToken cancellationToken);

    Task<SubscriptionPage> ListAsync(SubscriptionListQuery query, CancellationToken cancellationToken);

    /// <summary>Moves to another plan. The feature selection does not carry over — the new plan may price or include things differently.</summary>
    Task<Subscription> ChangePlanAsync(Guid id, Guid planId, CancellationToken cancellationToken);

    /// <summary>
    /// Replaces the selected optional features with exactly this set. A feature
    /// the plan does not let the customer toggle is refused, whoever is asking:
    /// platform staff change the plan, not the customer's rights under it.
    /// </summary>
    Task<Subscription> SetFeaturesAsync(Guid id, IReadOnlyCollection<Guid> featureIds, CancellationToken cancellationToken);

    Task<Subscription> CancelAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>
    /// Asks for the subscription to end when the paid period runs out rather than
    /// at once — the self-service customer's own cancel (docs/self-service-saas-plan.md §9).
    /// The subscription stays Active and entitling meanwhile; nothing is torn down.
    /// </summary>
    Task<Subscription> RequestCancelAtPeriodEndAsync(Guid id, CancellationToken cancellationToken);

    Task<Subscription> SuspendAsync(Guid id, CancellationToken cancellationToken);

    Task<Subscription> ActivateAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>
    /// Prices a plan and a feature selection without touching anything. The
    /// dashboard shows this before the customer commits, and it must agree with
    /// what the invoice will say, so both go through the same calculator.
    /// </summary>
    Task<QuoteView> QuoteAsync(Guid planId, IReadOnlyCollection<Guid> featureIds, CancellationToken cancellationToken);
}
