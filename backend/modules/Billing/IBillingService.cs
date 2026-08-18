using Billing.Domain;

namespace Billing;

public sealed record OpenBillingAccountInput(Guid CustomerId, string Currency, string BillingEmail, string? TaxId);

public sealed record InvoiceListQuery(int Page, int PageSize, Guid? CustomerId, InvoiceStatus? Status);

public sealed record InvoicePage(IReadOnlyCollection<Invoice> Items, int Page, int PageSize, long TotalCount);

public sealed record RecordPaymentInput(decimal Amount, string Currency, PaymentMethod Method, string? Reference, DateTimeOffset PaidAt);

/// <summary>
/// Billing as record-keeping.
///
/// KNIGHT prices a period, writes down what was charged, and records payments
/// somebody observed. It does not take payments, and nothing here should be read
/// as if it did (docs/domain-model.md section 6, risks.md R14).
/// </summary>
public interface IBillingService
{
    Task<BillingAccount> OpenAccountAsync(OpenBillingAccountInput input, CancellationToken cancellationToken);

    Task<BillingAccount?> GetAccountAsync(Guid customerId, CancellationToken cancellationToken);

    Task<InvoicePage> ListInvoicesAsync(InvoiceListQuery query, CancellationToken cancellationToken);

    Task<Invoice?> GetInvoiceAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>
    /// Builds — or rebuilds — the draft invoice for a subscription's current
    /// period from the same calculator that quotes it. Idempotent: calling it
    /// twice replaces the draft rather than charging twice.
    /// </summary>
    Task<Invoice> PrepareInvoiceAsync(Guid subscriptionId, CancellationToken cancellationToken);

    /// <summary>Freezes the invoice, numbers it, and rolls the subscription's period forward.</summary>
    Task<Invoice> IssueInvoiceAsync(Guid invoiceId, CancellationToken cancellationToken);

    Task<Invoice> RecordPaymentAsync(Guid invoiceId, RecordPaymentInput input, CancellationToken cancellationToken);

    Task<Invoice> VoidInvoiceAsync(Guid invoiceId, CancellationToken cancellationToken);
}
