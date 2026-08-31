using Knight.Domain.Common;
using Knight.Domain.Exceptions;

namespace PlatformBilling.Domain;

/// <summary>
/// A self-service checkout in progress: the plan a merchant chose, the optional
/// features they added, the interval, and the <b>authoritative</b> price KNIGHT
/// computed for it (docs/self-service-saas-plan.md §14). The amount is recorded
/// here, server-side, precisely so that a client-supplied price is never trusted
/// — the provider is asked to collect this figure, not one the browser sent.
///
/// The session is completed only when a verified provider event confirms it; a
/// browser landing on a success page never completes it
/// (docs/self-service-saas-plan.md §15, §22).
/// </summary>
public sealed class CheckoutSession : AuditableEntity, ICustomerOwned
{
    public Guid CustomerId { get; private set; }

    public Guid PlanId { get; private set; }

    /// <summary>The subscription this checkout will activate on payment. Created Pending alongside the session.</summary>
    public Guid SubscriptionId { get; private set; }

    public BillingInterval Interval { get; private set; }

    /// <summary>The optional features the merchant chose (CUSTOM plans), dependencies already folded in.</summary>
    public Guid[] SelectedFeatureIds { get; private set; }

    public decimal Amount { get; private set; }

    public string Currency { get; private set; }

    public string? Provider { get; private set; }

    public string? ProviderSessionId { get; private set; }

    public CheckoutSessionStatus Status { get; private set; }

    public DateTimeOffset ExpiresAt { get; private set; }

    public Money Total => Money.Of(Amount, Currency);

    private CheckoutSession()
    {
        Currency = string.Empty;
        SelectedFeatureIds = [];
    }

    private CheckoutSession(
        Guid id,
        DateTimeOffset createdAt,
        Guid customerId,
        Guid planId,
        Guid subscriptionId,
        BillingInterval interval,
        IEnumerable<Guid> selectedFeatureIds,
        Money total,
        DateTimeOffset expiresAt)
        : base(id, createdAt)
    {
        CustomerId = customerId;
        PlanId = planId;
        SubscriptionId = subscriptionId;
        Interval = interval;
        SelectedFeatureIds = selectedFeatureIds.Distinct().ToArray();
        Amount = total.Amount;
        Currency = total.Currency;
        ExpiresAt = expiresAt;
        Status = CheckoutSessionStatus.Open;
    }

    public static CheckoutSession Open(
        Guid id,
        DateTimeOffset createdAt,
        Guid customerId,
        Guid planId,
        Guid subscriptionId,
        BillingInterval interval,
        IEnumerable<Guid> selectedFeatureIds,
        Money total,
        DateTimeOffset expiresAt)
    {
        if (customerId == Guid.Empty)
        {
            throw DomainException.Validation("A checkout session must belong to a customer.");
        }

        if (planId == Guid.Empty)
        {
            throw DomainException.Validation("A checkout session must name a plan.");
        }

        if (subscriptionId == Guid.Empty)
        {
            throw DomainException.Validation("A checkout session must name the subscription it will activate.");
        }

        if (total.Amount < 0)
        {
            throw DomainException.Validation("A checkout total cannot be negative.");
        }

        if (expiresAt <= createdAt)
        {
            throw DomainException.Validation("A checkout session must expire in the future.");
        }

        return new CheckoutSession(id, createdAt, customerId, planId, subscriptionId, interval, selectedFeatureIds, total, expiresAt);
    }

    /// <summary>Records which provider is collecting this checkout, and the session id it issued.</summary>
    public void AttachProviderSession(string provider, string providerSessionId, DateTimeOffset now)
    {
        EnsureOpen();
        Provider = RequireText(provider, "provider", 50);
        ProviderSessionId = RequireText(providerSessionId, "provider session id", 200);
        MarkUpdated(now);
    }

    /// <summary>The provider confirmed payment. Terminal.</summary>
    public void Complete(DateTimeOffset now)
    {
        EnsureOpen();
        Status = CheckoutSessionStatus.Completed;
        MarkUpdated(now);
    }

    public void Cancel(DateTimeOffset now)
    {
        EnsureOpen();
        Status = CheckoutSessionStatus.Cancelled;
        MarkUpdated(now);
    }

    /// <summary>The window closed without payment. Idempotent: expiring an already-expired session is a no-op.</summary>
    public void Expire(DateTimeOffset now)
    {
        if (Status is CheckoutSessionStatus.Expired)
        {
            return;
        }

        EnsureOpen();
        Status = CheckoutSessionStatus.Expired;
        MarkUpdated(now);
    }

    public bool IsExpiredAt(DateTimeOffset moment) => moment >= ExpiresAt;

    private void EnsureOpen()
    {
        if (Status is not CheckoutSessionStatus.Open)
        {
            throw DomainException.Conflict($"A checkout session in status '{Status}' can no longer change.");
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

public enum BillingInterval
{
    Monthly = 0,
    Yearly = 1,
}

public enum CheckoutSessionStatus
{
    Open = 0,
    Completed = 1,
    Expired = 2,
    Cancelled = 3,
}
