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

    /// <summary>
    /// Invoices every active subscription whose period has closed, and rolls each
    /// one forward.
    ///
    /// This is the answer to "when does an invoice get made", which used to be
    /// nowhere: <see cref="PrepareInvoiceAsync"/> knew how to build one and
    /// nothing decided that it was time. Deliberately a separate operation rather
    /// than something hidden inside issuing, so the decision to bill is visible,
    /// auditable and testable on its own.
    ///
    /// Idempotent by construction. Preparing rebuilds the draft for a period
    /// rather than adding a second, and the period only rolls forward after an
    /// invoice for the old one exists — so a run that dies half way leaves a
    /// period that will be billed again, never one that is silently skipped.
    /// </summary>
    Task<BillingRunResult> RunAsync(CancellationToken cancellationToken);
}

/// <summary>
/// What one billing run did. <paramref name="Failed"/> is per subscription: one
/// customer's bad data must not stop everyone else being billed, so a failure is
/// recorded and the run carries on.
/// </summary>
public sealed record BillingRunResult(int Considered, int Invoiced, int Issued, int Failed);
