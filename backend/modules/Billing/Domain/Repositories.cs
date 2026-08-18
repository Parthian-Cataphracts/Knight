namespace Billing.Domain;

public interface IBillingAccountRepository
{
    Task<BillingAccount?> GetForCustomerAsync(Guid customerId, CancellationToken cancellationToken);

    Task AddAsync(BillingAccount account, CancellationToken cancellationToken);

    Task SaveChangesAsync(CancellationToken cancellationToken);
}

public interface IInvoiceRepository
{
    /// <summary>Loads the invoice with its lines and payments; both are part of the aggregate.</summary>
    Task<Invoice?> GetByIdAsync(Guid id, CancellationToken cancellationToken);

    Task<Invoice?> GetDraftForPeriodAsync(
        Guid customerId,
        DateTimeOffset periodStart,
        DateTimeOffset periodEnd,
        CancellationToken cancellationToken);

    Task<(IReadOnlyCollection<Invoice> Items, long TotalCount)> ListAsync(
        int page,
        int pageSize,
        Guid? customerId,
        InvoiceStatus? status,
        CancellationToken cancellationToken);

    Task AddAsync(Invoice invoice, CancellationToken cancellationToken);

    void RegisterNewLine(InvoiceLine line);

    void RemoveLine(InvoiceLine line);

    void RegisterNewPayment(PaymentRecord payment);

    /// <summary>
    /// Reserves the next invoice number. Sequential and gapless per year, which is
    /// what accounting expects, so it is the database's job rather than a count of
    /// existing rows that two concurrent callers could both read.
    /// </summary>
    Task<string> ReserveNumberAsync(int year, CancellationToken cancellationToken);

    Task SaveChangesAsync(CancellationToken cancellationToken);
}
