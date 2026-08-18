using Knight.Domain.Common;
using Knight.Domain.Exceptions;

namespace Billing.Domain;

/// <summary>
/// What a customer was charged for one billing period.
///
/// A draft is a working document and can be rebuilt freely. Issuing it freezes
/// it: from that moment the lines, the totals and the currency are a historical
/// record of what was charged, and no later change to a plan, a price or a
/// subscription may alter them. Correcting an issued invoice means voiding it and
/// issuing another, which is why there is no edit path here.
/// </summary>
public sealed class Invoice : AuditableEntity, ICustomerOwned
{
    public Guid CustomerId { get; private set; }

    public Guid? SubscriptionId { get; private set; }

    /// <summary>Human-facing sequential reference, assigned at issue time.</summary>
    public string? Number { get; private set; }

    public DateTimeOffset PeriodStart { get; private set; }

    public DateTimeOffset PeriodEnd { get; private set; }

    public decimal Subtotal { get; private set; }

    public decimal Tax { get; private set; }

    public decimal Total { get; private set; }

    public string Currency { get; private set; }

    public InvoiceStatus Status { get; private set; }

    public DateTimeOffset? IssuedAt { get; private set; }

    public DateTimeOffset? DueAt { get; private set; }

    public DateTimeOffset? PaidAt { get; private set; }

    private readonly List<InvoiceLine> _lines = [];

    public IReadOnlyCollection<InvoiceLine> Lines => _lines.AsReadOnly();

    private readonly List<PaymentRecord> _payments = [];

    public IReadOnlyCollection<PaymentRecord> Payments => _payments.AsReadOnly();

    private Invoice()
    {
        Currency = string.Empty;
    }

    private Invoice(
        Guid id,
        DateTimeOffset createdAt,
        Guid customerId,
        Guid? subscriptionId,
        DateTimeOffset periodStart,
        DateTimeOffset periodEnd,
        string currency)
        : base(id, createdAt)
    {
        CustomerId = customerId;
        SubscriptionId = subscriptionId;
        PeriodStart = periodStart;
        PeriodEnd = periodEnd;
        Currency = currency;
        Status = InvoiceStatus.Draft;
    }

    public static Invoice Draft(
        Guid id,
        DateTimeOffset createdAt,
        Guid customerId,
        Guid? subscriptionId,
        DateTimeOffset periodStart,
        DateTimeOffset periodEnd,
        string currency)
    {
        if (customerId == Guid.Empty)
        {
            throw DomainException.Validation("An invoice must belong to a customer.");
        }

        if (periodEnd <= periodStart)
        {
            throw DomainException.Validation("An invoice period must end after it starts.");
        }

        return new Invoice(id, createdAt, customerId, subscriptionId, periodStart, periodEnd, Money.Zero(currency).Currency);
    }

    public InvoiceLine AddLine(string description, Guid? featureId, int quantity, Money unitPrice, DateTimeOffset now)
    {
        EnsureDraft();

        if (!string.Equals(unitPrice.Currency, Currency, StringComparison.Ordinal))
        {
            throw DomainException.Conflict($"The line is priced in '{unitPrice.Currency}' but the invoice is in '{Currency}'.");
        }

        var line = InvoiceLine.Create(Id, description, featureId, quantity, unitPrice);
        _lines.Add(line);
        Recalculate();
        MarkUpdated(now);
        return line;
    }

    public void ClearLines(DateTimeOffset now)
    {
        EnsureDraft();

        _lines.Clear();
        Recalculate();
        MarkUpdated(now);
    }

    /// <summary>
    /// Sets the tax figure. KNIGHT does not compute tax — jurisdictions differ and
    /// getting it wrong is a legal matter, not a rounding one — so an operator or
    /// an external system supplies it before the invoice is issued.
    /// </summary>
    public void SetTax(Money tax, DateTimeOffset now)
    {
        EnsureDraft();

        if (!string.Equals(tax.Currency, Currency, StringComparison.Ordinal))
        {
            throw DomainException.Conflict($"Tax is in '{tax.Currency}' but the invoice is in '{Currency}'.");
        }

        Tax = tax.Amount;
        Recalculate();
        MarkUpdated(now);
    }

    /// <summary>Freezes the invoice and assigns its number. Nothing about it may change afterwards.</summary>
    public void Issue(string number, DateTimeOffset now, DateTimeOffset dueAt)
    {
        EnsureDraft();

        if (_lines.Count == 0)
        {
            throw DomainException.Conflict("An invoice with no lines cannot be issued.");
        }

        if (string.IsNullOrWhiteSpace(number))
        {
            throw DomainException.Validation("An issued invoice requires a number.");
        }

        if (dueAt <= now)
        {
            throw DomainException.Validation("An invoice cannot fall due before it is issued.");
        }

        Number = number.Trim();
        Status = InvoiceStatus.Issued;
        IssuedAt = now;
        DueAt = dueAt;
        MarkUpdated(now);
    }

    /// <summary>
    /// Records a payment against the invoice. Recording is all this is: no money
    /// moves, and the platform is not claiming it did.
    /// </summary>
    public PaymentRecord RecordPayment(
        Money amount,
        PaymentMethod method,
        string? reference,
        Guid? recordedBy,
        DateTimeOffset paidAt)
    {
        if (Status is InvoiceStatus.Draft)
        {
            throw DomainException.Conflict("A draft invoice cannot be paid; issue it first.");
        }

        if (Status is InvoiceStatus.Void)
        {
            throw DomainException.Conflict("A void invoice cannot be paid.");
        }

        if (!string.Equals(amount.Currency, Currency, StringComparison.Ordinal))
        {
            throw DomainException.Conflict($"The payment is in '{amount.Currency}' but the invoice is in '{Currency}'.");
        }

        if (amount.IsZero)
        {
            throw DomainException.Validation("A payment must be for more than nothing.");
        }

        var payment = PaymentRecord.Create(Id, amount, method, reference, recordedBy, paidAt);
        _payments.Add(payment);

        // Overpayment is recorded rather than refused: the money arrived, and
        // hiding that would make the ledger disagree with the bank.
        if (PaidAmount >= Total)
        {
            Status = InvoiceStatus.Paid;
            PaidAt = paidAt;
        }

        MarkUpdated(paidAt);
        return payment;
    }

    /// <summary>Marks an unpaid, issued invoice overdue. Reversible only by paying or voiding it.</summary>
    public void MarkOverdue(DateTimeOffset now)
    {
        if (Status is not InvoiceStatus.Issued)
        {
            throw DomainException.Conflict($"An invoice in status '{Status}' cannot fall overdue.");
        }

        Status = InvoiceStatus.Overdue;
        MarkUpdated(now);
    }

    /// <summary>
    /// Cancels the invoice without deleting it. A paid invoice cannot be voided —
    /// that would make the money unaccounted for; refunding is a separate fact.
    /// </summary>
    public void Void(DateTimeOffset now)
    {
        if (Status is InvoiceStatus.Paid)
        {
            throw DomainException.Conflict("A paid invoice cannot be voided.");
        }

        if (Status is InvoiceStatus.Void)
        {
            throw DomainException.Conflict("The invoice is already void.");
        }

        Status = InvoiceStatus.Void;
        MarkUpdated(now);
    }

    public decimal PaidAmount => _payments.Sum(payment => payment.Amount);

    public decimal OutstandingAmount => Math.Max(0m, Total - PaidAmount);

    public bool IsSettled => Status is InvoiceStatus.Paid or InvoiceStatus.Void;

    private void Recalculate()
    {
        Subtotal = _lines.Sum(line => line.Total);
        Total = Subtotal + Tax;
    }

    private void EnsureDraft()
    {
        if (Status is not InvoiceStatus.Draft)
        {
            throw DomainException.Conflict("An issued invoice is a historical record and cannot be changed.");
        }
    }
}

public enum InvoiceStatus
{
    Draft = 0,
    Issued = 1,
    Paid = 2,
    Void = 3,
    Overdue = 4,
}
