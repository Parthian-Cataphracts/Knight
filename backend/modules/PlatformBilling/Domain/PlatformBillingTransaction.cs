using Knight.Domain.Common;
using Knight.Domain.Exceptions;

namespace PlatformBilling.Domain;

/// <summary>
/// One movement of money in KNIGHT's own billing — a merchant paying KNIGHT for a
/// plan (docs/self-service-saas-plan.md §3, §5.5). This is <b>not</b> a store's
/// own payment gateway: it never touches, reads or configures what a merchant
/// charges their end customers.
///
/// The transaction is the record a provider webhook writes against, and the
/// <see cref="IdempotencyKey"/> is what makes a webhook safe to deliver twice: a
/// replay finds the existing row and changes nothing rather than charging or
/// activating a second time (docs/self-service-saas-plan.md §15, §27).
/// </summary>
public sealed class PlatformBillingTransaction : AuditableEntity, ICustomerOwned
{
    public Guid CustomerId { get; private set; }

    public Guid SubscriptionId { get; private set; }

    /// <summary>The provider that moved the money (e.g. the configured gateway), and the id it knows it by.</summary>
    public string Provider { get; private set; }

    public string? ProviderTransactionId { get; private set; }

    public decimal Amount { get; private set; }

    public string Currency { get; private set; }

    public PlatformBillingTransactionStatus Status { get; private set; }

    /// <summary>The caller's/provider's key, unique per logical charge, so a replay is detectable.</summary>
    public string IdempotencyKey { get; private set; }

    public decimal RefundedAmount { get; private set; }

    public Money Charged => Money.Of(Amount, Currency);

    private PlatformBillingTransaction()
    {
        Provider = string.Empty;
        Currency = string.Empty;
        IdempotencyKey = string.Empty;
    }

    private PlatformBillingTransaction(
        Guid id,
        DateTimeOffset createdAt,
        Guid customerId,
        Guid subscriptionId,
        string provider,
        Money amount,
        string idempotencyKey)
        : base(id, createdAt)
    {
        CustomerId = customerId;
        SubscriptionId = subscriptionId;
        Provider = provider;
        Amount = amount.Amount;
        Currency = amount.Currency;
        IdempotencyKey = idempotencyKey;
        Status = PlatformBillingTransactionStatus.Pending;
    }

    /// <summary>
    /// Opens a pending charge for a checkout. It confirms nothing on its own — only
    /// a verified provider event moves it to <see cref="PlatformBillingTransactionStatus.Succeeded"/>.
    /// </summary>
    public static PlatformBillingTransaction Record(
        Guid id,
        DateTimeOffset createdAt,
        Guid customerId,
        Guid subscriptionId,
        string provider,
        Money amount,
        string idempotencyKey)
    {
        if (customerId == Guid.Empty)
        {
            throw DomainException.Validation("A platform billing transaction must belong to a customer.");
        }

        if (subscriptionId == Guid.Empty)
        {
            throw DomainException.Validation("A platform billing transaction must name a subscription.");
        }

        if (amount.Amount < 0)
        {
            throw DomainException.Validation("A charge cannot be negative.");
        }

        return new PlatformBillingTransaction(
            id,
            createdAt,
            customerId,
            subscriptionId,
            RequireText(provider, "provider", 50),
            amount,
            RequireText(idempotencyKey, "idempotency key", 200));
    }

    /// <summary>Records the provider's id for this charge, once the provider has assigned one.</summary>
    public void AttachProviderTransaction(string providerTransactionId, DateTimeOffset now)
    {
        ProviderTransactionId = RequireText(providerTransactionId, "provider transaction id", 200);
        MarkUpdated(now);
    }

    public void Succeed(DateTimeOffset now)
    {
        if (Status is not PlatformBillingTransactionStatus.Pending)
        {
            throw DomainException.Conflict($"A transaction in status '{Status}' cannot succeed.");
        }

        Status = PlatformBillingTransactionStatus.Succeeded;
        MarkUpdated(now);
    }

    public void Fail(DateTimeOffset now)
    {
        if (Status is not PlatformBillingTransactionStatus.Pending)
        {
            throw DomainException.Conflict($"A transaction in status '{Status}' cannot fail.");
        }

        Status = PlatformBillingTransactionStatus.Failed;
        MarkUpdated(now);
    }

    /// <summary>
    /// Records a refund against a succeeded charge. A refund for the whole amount
    /// leaves it <see cref="PlatformBillingTransactionStatus.Refunded"/>; a
    /// smaller one leaves it <see cref="PlatformBillingTransactionStatus.PartiallyRefunded"/>
    /// and may be called again up to the amount charged.
    /// </summary>
    public void Refund(Money amount, DateTimeOffset now)
    {
        if (Status is not (PlatformBillingTransactionStatus.Succeeded or PlatformBillingTransactionStatus.PartiallyRefunded))
        {
            throw DomainException.Conflict($"A transaction in status '{Status}' cannot be refunded.");
        }

        if (!string.Equals(amount.Currency, Currency, StringComparison.OrdinalIgnoreCase))
        {
            throw DomainException.Validation("A refund must be in the currency of the charge.");
        }

        if (amount.Amount <= 0)
        {
            throw DomainException.Validation("A refund must be for a positive amount.");
        }

        if (RefundedAmount + amount.Amount > Amount)
        {
            throw DomainException.Conflict("A refund cannot exceed the amount charged.");
        }

        RefundedAmount += amount.Amount;
        Status = RefundedAmount >= Amount
            ? PlatformBillingTransactionStatus.Refunded
            : PlatformBillingTransactionStatus.PartiallyRefunded;
        MarkUpdated(now);
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

public enum PlatformBillingTransactionStatus
{
    Pending = 0,
    Succeeded = 1,
    Failed = 2,
    Refunded = 3,
    PartiallyRefunded = 4,
}
