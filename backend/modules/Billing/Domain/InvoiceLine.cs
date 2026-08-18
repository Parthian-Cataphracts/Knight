using Knight.Domain.Common;
using Knight.Domain.Exceptions;

namespace Billing.Domain;

/// <summary>
/// One charge on an invoice.
///
/// The description is stored rather than derived from the feature or plan it came
/// from: an invoice must still read correctly after the feature is renamed or the
/// plan retired.
/// </summary>
public sealed class InvoiceLine : Entity
{
    public Guid InvoiceId { get; private set; }

    public string Description { get; private set; }

    /// <summary>Kept for reporting; the line stands on its own without it.</summary>
    public Guid? FeatureId { get; private set; }

    public int Quantity { get; private set; }

    public decimal UnitPrice { get; private set; }

    public decimal Total { get; private set; }

    private InvoiceLine()
    {
        Description = string.Empty;
    }

    private InvoiceLine(Guid id, Guid invoiceId, string description, Guid? featureId, int quantity, Money unitPrice)
        : base(id)
    {
        InvoiceId = invoiceId;
        Description = description;
        FeatureId = featureId;
        Quantity = quantity;
        UnitPrice = unitPrice.Amount;
        Total = unitPrice.Multiply(quantity).Amount;
    }

    internal static InvoiceLine Create(Guid invoiceId, string description, Guid? featureId, int quantity, Money unitPrice)
    {
        if (string.IsNullOrWhiteSpace(description))
        {
            throw DomainException.Validation("An invoice line requires a description.");
        }

        if (quantity < 1)
        {
            throw DomainException.Validation("An invoice line must be for at least one unit.");
        }

        var trimmed = description.Trim();
        if (trimmed.Length > 300)
        {
            throw DomainException.Validation("An invoice line description must be 300 characters or fewer.");
        }

        return new InvoiceLine(Guid.NewGuid(), invoiceId, trimmed, featureId, quantity, unitPrice);
    }
}
