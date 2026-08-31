using Knight.Domain.Common;
using Knight.Domain.Exceptions;

namespace Subscriptions.Domain;

/// <summary>
/// A customer's commercial relationship with a plan, over time.
///
/// The subscription owns which optional features the customer has chosen, but
/// not whether they are allowed to choose them — that is the plan's business,
/// checked by the application service before it calls in here. What the
/// aggregate does guarantee is that the lifecycle cannot be walked backwards:
/// a cancelled subscription never resumes, and features cannot be changed on one
/// that is no longer running.
/// </summary>
public sealed class Subscription : AuditableEntity, ICustomerOwned
{
    public Guid CustomerId { get; private set; }

    public Guid PlanId { get; private set; }

    public SubscriptionStatus Status { get; private set; }

    public DateTimeOffset StartedAt { get; private set; }

    public DateTimeOffset CurrentPeriodStart { get; private set; }

    public DateTimeOffset CurrentPeriodEnd { get; private set; }

    public DateTimeOffset? CancelledAt { get; private set; }

    /// <summary>
    /// True once the customer has asked to end the subscription when the paid
    /// period runs out. The subscription stays <see cref="SubscriptionStatus.Active"/>
    /// meanwhile: cancelling is a future intent, not an immediate teardown
    /// (docs/self-service-saas-plan.md §9).
    /// </summary>
    public bool CancelAtPeriodEnd { get; private set; }

    /// <summary>
    /// The platform-billing provider that owns this subscription's money
    /// (self-service only), and the id it knows it by. Null for an
    /// operator-created subscription that was never taken through a provider.
    /// This is KNIGHT's own billing, never a store's payment gateway
    /// (docs/self-service-saas-plan.md §3).
    /// </summary>
    public string? Provider { get; private set; }

    public string? ProviderSubscriptionId { get; private set; }

    private readonly List<SubscriptionFeature> _features = [];

    public IReadOnlyCollection<SubscriptionFeature> Features => _features.AsReadOnly();

    private Subscription()
    {
    }

    private Subscription(
        Guid id,
        DateTimeOffset createdAt,
        Guid customerId,
        Guid planId,
        SubscriptionStatus status,
        DateTimeOffset startedAt,
        DateTimeOffset periodStart,
        DateTimeOffset periodEnd)
        : base(id, createdAt)
    {
        CustomerId = customerId;
        PlanId = planId;
        Status = status;
        StartedAt = startedAt;
        CurrentPeriodStart = periodStart;
        CurrentPeriodEnd = periodEnd;
    }

    public static Subscription Start(
        Guid id,
        DateTimeOffset createdAt,
        Guid customerId,
        Guid planId,
        DateTimeOffset periodStart,
        DateTimeOffset periodEnd,
        bool asTrial = false)
    {
        if (customerId == Guid.Empty)
        {
            throw DomainException.Validation("A subscription must belong to a customer.");
        }

        if (planId == Guid.Empty)
        {
            throw DomainException.Validation("A subscription must name a plan.");
        }

        if (periodEnd <= periodStart)
        {
            throw DomainException.Validation("A billing period must end after it starts.");
        }

        return new Subscription(
            id,
            createdAt,
            customerId,
            planId,
            asTrial ? SubscriptionStatus.Trial : SubscriptionStatus.Active,
            periodStart,
            periodStart,
            periodEnd);
    }

    /// <summary>
    /// Begins a subscription that is awaiting its first platform payment. It
    /// entitles the customer to nothing (see <see cref="IsEntitling"/>) until a
    /// verified payment activates it: in self-service billing a browser redirect
    /// never grants access — only the provider's webhook does
    /// (docs/self-service-saas-plan.md §7). The customer may still select
    /// optional features on it before paying, which is what the checkout prices.
    /// </summary>
    public static Subscription StartPending(
        Guid id,
        DateTimeOffset createdAt,
        Guid customerId,
        Guid planId,
        DateTimeOffset periodStart,
        DateTimeOffset periodEnd)
    {
        ValidateStart(customerId, planId, periodStart, periodEnd);

        return new Subscription(
            id,
            createdAt,
            customerId,
            planId,
            SubscriptionStatus.Pending,
            periodStart,
            periodStart,
            periodEnd);
    }

    private static void ValidateStart(Guid customerId, Guid planId, DateTimeOffset periodStart, DateTimeOffset periodEnd)
    {
        if (customerId == Guid.Empty)
        {
            throw DomainException.Validation("A subscription must belong to a customer.");
        }

        if (planId == Guid.Empty)
        {
            throw DomainException.Validation("A subscription must name a plan.");
        }

        if (periodEnd <= periodStart)
        {
            throw DomainException.Validation("A billing period must end after it starts.");
        }
    }

    /// <summary>
    /// Records which platform-billing provider owns this subscription and the id
    /// it is known by there, so a webhook can find it and a refund can be traced.
    /// </summary>
    public void LinkProvider(string provider, string providerSubscriptionId, DateTimeOffset now)
    {
        Provider = RequireText(provider, "billing provider", 50);
        ProviderSubscriptionId = RequireText(providerSubscriptionId, "provider subscription id", 200);
        MarkUpdated(now);
    }

    /// <summary>
    /// Marks the subscription to end when the paid period runs out rather than at
    /// once. Nothing is torn down here; the store keeps running until the period
    /// closes (docs/self-service-saas-plan.md §9).
    /// </summary>
    public void RequestCancelAtPeriodEnd(DateTimeOffset now)
    {
        EnsureChangeable();
        CancelAtPeriodEnd = true;
        MarkUpdated(now);
    }

    // --- Lifecycle -------------------------------------------------------
    //
    // Pending ─┐   (self-service: activated only by a verified payment)
    // Trial ───┤
    //          ├──> Active <──> PastDue ──> Suspended
    //          │        │            │           │
    //          └────────┴────────────┴───────────┴──> Cancelled (terminal)

    /// <summary>
    /// Brings a subscription into good standing: activates a paid self-service
    /// sign-up (from <see cref="SubscriptionStatus.Pending"/>), converts a trial,
    /// or recovers a lapsed one.
    /// </summary>
    public void Activate(DateTimeOffset now)
    {
        if (Status is not (SubscriptionStatus.Pending or SubscriptionStatus.Trial or SubscriptionStatus.PastDue or SubscriptionStatus.Suspended))
        {
            throw DomainException.Conflict($"A subscription in status '{Status}' cannot be activated.");
        }

        Status = SubscriptionStatus.Active;
        MarkUpdated(now);
    }

    /// <summary>
    /// An invoice went unpaid. Deliberately distinct from suspension: the
    /// customer still has their capabilities while the payment is chased.
    /// </summary>
    public void MarkPastDue(DateTimeOffset now)
    {
        if (Status is not (SubscriptionStatus.Active or SubscriptionStatus.Trial))
        {
            throw DomainException.Conflict($"A subscription in status '{Status}' cannot be marked past due.");
        }

        Status = SubscriptionStatus.PastDue;
        MarkUpdated(now);
    }

    public void Suspend(DateTimeOffset now)
    {
        if (Status is SubscriptionStatus.Cancelled)
        {
            throw DomainException.Conflict("A cancelled subscription cannot be suspended.");
        }

        Status = SubscriptionStatus.Suspended;
        MarkUpdated(now);
    }

    /// <summary>Terminal. The rows stay for billing history; the relationship does not resume.</summary>
    public void Cancel(DateTimeOffset now)
    {
        if (Status is SubscriptionStatus.Cancelled)
        {
            throw DomainException.Conflict("The subscription is already cancelled.");
        }

        Status = SubscriptionStatus.Cancelled;
        CancelledAt = now;
        MarkUpdated(now);
    }

    /// <summary>
    /// Moves to a different plan. The feature selection is cleared rather than
    /// carried over: what was selectable on the old plan may be included,
    /// unavailable, or priced differently on the new one, and guessing which
    /// would silently change what the customer pays for.
    /// </summary>
    public void ChangePlan(Guid planId, DateTimeOffset now)
    {
        EnsureChangeable();

        if (planId == Guid.Empty)
        {
            throw DomainException.Validation("A subscription must name a plan.");
        }

        if (planId == PlanId)
        {
            throw DomainException.Conflict("The subscription is already on that plan.");
        }

        PlanId = planId;
        _features.Clear();
        MarkUpdated(now);
    }

    /// <summary>Rolls the billing period forward, which is what an issued invoice for the period does.</summary>
    public void AdvancePeriod(DateTimeOffset periodEnd, DateTimeOffset now)
    {
        EnsureChangeable();

        if (periodEnd <= CurrentPeriodEnd)
        {
            throw DomainException.Validation("The next period must end after the current one.");
        }

        CurrentPeriodStart = CurrentPeriodEnd;
        CurrentPeriodEnd = periodEnd;
        MarkUpdated(now);
    }

    // --- Feature selection ------------------------------------------------

    public SubscriptionFeature EnableFeature(Guid featureId, Guid? enabledBy, DateTimeOffset now)
    {
        EnsureChangeable();

        var existing = _features.SingleOrDefault(f => f.FeatureId == featureId);
        if (existing is not null)
        {
            existing.Enable(enabledBy, now);
            MarkUpdated(now);
            return existing;
        }

        var selection = SubscriptionFeature.Create(Id, featureId, enabledBy, now);
        _features.Add(selection);
        MarkUpdated(now);
        return selection;
    }

    public void DisableFeature(Guid featureId, DateTimeOffset now)
    {
        EnsureChangeable();

        var existing = _features.SingleOrDefault(f => f.FeatureId == featureId)
            ?? throw DomainException.Conflict("The subscription does not include this feature.");

        existing.Disable(now);
        MarkUpdated(now);
    }

    public IReadOnlyCollection<Guid> EnabledFeatureIds =>
        _features.Where(f => f.IsEnabled).Select(f => f.FeatureId).ToArray();

    public bool HasFeatureEnabled(Guid featureId) => _features.Any(f => f.FeatureId == featureId && f.IsEnabled);

    /// <summary>
    /// True when the subscription entitles the customer to anything at all. A
    /// suspended or cancelled subscription does not, which is what makes
    /// entitlement resolution able to answer without consulting anything else.
    /// </summary>
    public bool IsEntitling => Status is SubscriptionStatus.Trial or SubscriptionStatus.Active or SubscriptionStatus.PastDue;

    private void EnsureChangeable()
    {
        if (Status is SubscriptionStatus.Cancelled)
        {
            throw DomainException.Conflict("A cancelled subscription cannot be changed.");
        }
    }

    private static string RequireText(string value, string what, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw DomainException.Validation($"A {what} is required.");
        }

        var trimmed = value.Trim();
        return trimmed.Length <= maxLength ? trimmed : trimmed[..maxLength];
    }
}

public enum SubscriptionStatus
{
    Trial = 0,
    Active = 1,
    PastDue = 2,
    Suspended = 3,
    Cancelled = 4,

    /// <summary>
    /// Created for a self-service checkout and awaiting its first verified
    /// payment. Entitles the customer to nothing until activated.
    /// </summary>
    Pending = 5,
}
