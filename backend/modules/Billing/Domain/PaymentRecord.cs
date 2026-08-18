using Knight.Domain.Common;
using Knight.Domain.Exceptions;

namespace Billing.Domain;

/// <summary>
/// A payment somebody observed and wrote down.
///
/// This is a record, not a transaction: KNIGHT does not move money in these
/// phases, so there is no gateway, no authorisation and no capture here. The
/// method and reference are there so the entry can be reconciled against a bank
/// statement by a human.
/// </summary>
public sealed class PaymentRecord : Entity
{
    public Guid InvoiceId { get; private set; }

    public decimal Amount { get; private set; }

    public string Currency { get; private set; }

    public PaymentMethod Method { get; private set; }

    /// <summary>Bank reference, transfer id or receipt number — whatever ties this to the real movement.</summary>
    public string? Reference { get; private set; }

    public DateTimeOffset PaidAt { get; private set; }

    /// <summary>The account that recorded it, so an entry is always attributable.</summary>
    public Guid? RecordedBy { get; private set; }

    private PaymentRecord()
    {
        Currency = string.Empty;
    }

    private PaymentRecord(
        Guid id,
        Guid invoiceId,
        Money amount,
        PaymentMethod method,
        string? reference,
        Guid? recordedBy,
        DateTimeOffset paidAt)
        : base(id)
    {
        InvoiceId = invoiceId;
        Amount = amount.Amount;
        Currency = amount.Currency;
        Method = method;
        Reference = reference;
        RecordedBy = recordedBy;
        PaidAt = paidAt;
    }

    internal static PaymentRecord Create(
        Guid invoiceId,
        Money amount,
        PaymentMethod method,
        string? reference,
        Guid? recordedBy,
        DateTimeOffset paidAt)
    {
        var trimmed = string.IsNullOrWhiteSpace(reference) ? null : reference.Trim();
        if (trimmed is { Length: > 200 })
        {
            throw DomainException.Validation("A payment reference must be 200 characters or fewer.");
        }

        return new PaymentRecord(Guid.NewGuid(), invoiceId, amount, method, trimmed, recordedBy, paidAt);
    }
}

public enum PaymentMethod
{
    BankTransfer = 0,
    Card = 1,
    Cash = 2,
    Credit = 3,
    Other = 4,
}
