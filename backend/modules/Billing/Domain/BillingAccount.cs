using Knight.Domain.Common;
using Knight.Domain.Exceptions;

namespace Billing.Domain;

/// <summary>
/// Where a customer's invoices go, and in what currency.
///
/// KNIGHT records billing facts; it does not process payments in these phases
/// (docs/domain-model.md section 6, risks.md R14). There is therefore no card,
/// no mandate and no gateway reference here — a payment is something an operator
/// records as having happened, not something this system makes happen.
/// </summary>
public sealed class BillingAccount : AuditableEntity, ICustomerOwned
{
    public Guid CustomerId { get; private set; }

    public string Currency { get; private set; }

    public string BillingEmail { get; private set; }

    public string? TaxId { get; private set; }

    private BillingAccount()
    {
        Currency = string.Empty;
        BillingEmail = string.Empty;
    }

    private BillingAccount(Guid id, DateTimeOffset createdAt, Guid customerId, string currency, string billingEmail)
        : base(id, createdAt)
    {
        CustomerId = customerId;
        Currency = currency;
        BillingEmail = billingEmail;
    }

    public static BillingAccount Open(
        Guid id,
        DateTimeOffset createdAt,
        Guid customerId,
        string currency,
        string billingEmail)
    {
        if (customerId == Guid.Empty)
        {
            throw DomainException.Validation("A billing account must belong to a customer.");
        }

        return new BillingAccount(
            id,
            createdAt,
            customerId,

            // Currency is validated by going through Money, so the account cannot
            // hold something invoices could not be denominated in.
            Money.Zero(currency).Currency,
            ValidateEmail(billingEmail));
    }

    public void UpdateDetails(string billingEmail, string? taxId, DateTimeOffset now)
    {
        BillingEmail = ValidateEmail(billingEmail);
        TaxId = string.IsNullOrWhiteSpace(taxId) ? null : taxId.Trim();
        MarkUpdated(now);
    }

    /// <summary>
    /// Changing the currency affects future invoices only. Issued invoices keep
    /// the currency they were issued in — an invoice is a record of what was
    /// charged, not a view over current settings.
    /// </summary>
    public void ChangeCurrency(string currency, DateTimeOffset now)
    {
        Currency = Money.Zero(currency).Currency;
        MarkUpdated(now);
    }

    private static string ValidateEmail(string? email)
    {
        if (string.IsNullOrWhiteSpace(email))
        {
            throw DomainException.Validation("A billing email is required.");
        }

        var trimmed = email.Trim();
        if (trimmed.Length > 320)
        {
            throw DomainException.Validation("Billing email cannot exceed 320 characters.");
        }

        if (!trimmed.Contains('@', StringComparison.Ordinal))
        {
            throw DomainException.Validation("Billing email is not a valid address.");
        }

        return trimmed;
    }
}
