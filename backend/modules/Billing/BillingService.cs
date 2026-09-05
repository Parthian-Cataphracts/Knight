using Billing.Domain;
using Knight.Application.Abstractions.ControlPlane;
using Knight.Application.Abstractions.Time;
using Knight.Application.Exceptions;
using Knight.Domain.Common;
using Microsoft.Extensions.Options;

namespace Billing;

/// <summary>
/// Invoicing and payment records.
///
/// The invoice is built from the same pricing port that quotes a subscription, so
/// what a customer was shown and what they are charged cannot drift apart. Once
/// issued it is frozen, and correcting it means voiding and reissuing.
///
/// Issuing deliberately does not roll the subscription's billing period forward.
/// A billing run that decides *when* to invoice is a scheduled-work concern and
/// is not part of this phase; leaving the period alone keeps the two decisions
/// separable instead of hiding one inside the other.
/// </summary>
internal sealed class BillingService : IBillingService
{
    private const int MaxPageSize = 100;

    private readonly IInvoiceRepository _invoices;
    private readonly IBillingAccountRepository _accounts;
    private readonly ISubscriptionReader _subscriptions;
    private readonly ISubscriptionPeriodWriter _periods;
    private readonly IPricingReader _pricing;
    private readonly IAuditTrail _audit;
    private readonly IAuditContext _actor;
    private readonly IDateTimeProvider _clock;
    private readonly BillingOptions _options;

    public BillingService(
        IInvoiceRepository invoices,
        IBillingAccountRepository accounts,
        ISubscriptionReader subscriptions,
        ISubscriptionPeriodWriter periods,
        IPricingReader pricing,
        IAuditTrail audit,
        IAuditContext actor,
        IDateTimeProvider clock,
        IOptions<BillingOptions> options)
    {
        _invoices = invoices;
        _accounts = accounts;
        _subscriptions = subscriptions;
        _periods = periods;
        _pricing = pricing;
        _audit = audit;
        _actor = actor;
        _clock = clock;
        _options = options.Value;
    }

    public async Task<BillingAccount> OpenAccountAsync(OpenBillingAccountInput input, CancellationToken cancellationToken)
    {
        var now = _clock.UtcNow;

        var existing = await _accounts.GetForCustomerAsync(input.CustomerId, cancellationToken);
        if (existing is not null)
        {
            existing.UpdateDetails(input.BillingEmail, input.TaxId, now);
            existing.ChangeCurrency(input.Currency, now);
            await _accounts.SaveChangesAsync(cancellationToken);
            await AuditAccountAsync("billing.account_updated", existing, cancellationToken);
            return existing;
        }

        var account = BillingAccount.Open(Guid.NewGuid(), now, input.CustomerId, input.Currency, input.BillingEmail);
        account.UpdateDetails(input.BillingEmail, input.TaxId, now);

        await _accounts.AddAsync(account, cancellationToken);
        await _accounts.SaveChangesAsync(cancellationToken);

        await AuditAccountAsync("billing.account_opened", account, cancellationToken);
        return account;
    }

    public Task<BillingAccount?> GetAccountAsync(Guid customerId, CancellationToken cancellationToken) =>
        _accounts.GetForCustomerAsync(customerId, cancellationToken);

    public async Task<InvoicePage> ListInvoicesAsync(InvoiceListQuery query, CancellationToken cancellationToken)
    {
        var page = query.Page < 1 ? 1 : query.Page;
        var pageSize = query.PageSize is < 1 or > MaxPageSize ? 25 : query.PageSize;

        var (items, total) = await _invoices.ListAsync(page, pageSize, query.CustomerId, query.Status, cancellationToken);
        return new InvoicePage(items, page, pageSize, total);
    }

    public Task<Invoice?> GetInvoiceAsync(Guid id, CancellationToken cancellationToken) =>
        _invoices.GetByIdAsync(id, cancellationToken);

    public async Task<Invoice> PrepareInvoiceAsync(Guid subscriptionId, CancellationToken cancellationToken)
    {
        var now = _clock.UtcNow;

        var subscription = await _subscriptions.GetAsync(subscriptionId, cancellationToken)
            ?? throw new NotFoundException($"Subscription '{subscriptionId}' was not found.");

        var quoted = await _pricing.QuoteAsync(
            subscription.PlanId,
            subscription.EnabledFeatureIds,
            subscription.CurrentPeriodStart,
            cancellationToken);

        // Rebuilding is the normal case: a draft is a working document, and the
        // selection or the prices may have changed since it was first prepared.
        var invoice = await _invoices.GetDraftForPeriodAsync(
            subscription.CustomerId,
            subscription.CurrentPeriodStart,
            subscription.CurrentPeriodEnd,
            cancellationToken);

        if (invoice is null)
        {
            invoice = Invoice.Draft(
                Guid.NewGuid(),
                now,
                subscription.CustomerId,
                subscription.SubscriptionId,
                subscription.CurrentPeriodStart,
                subscription.CurrentPeriodEnd,
                quoted.Currency);

            await _invoices.AddAsync(invoice, cancellationToken);
        }
        else
        {
            foreach (var line in invoice.Lines.ToArray())
            {
                _invoices.RemoveLine(line);
            }

            invoice.ClearLines(now);
        }

        foreach (var line in quoted.Lines)
        {
            var added = invoice.AddLine(
                line.Description,
                line.FeatureId,
                line.Quantity,
                Money.Of(line.UnitPrice, quoted.Currency),
                now);

            _invoices.RegisterNewLine(added);
        }

        ApplyConfiguredTax(invoice, now);

        await _invoices.SaveChangesAsync(cancellationToken);

        await AuditInvoiceAsync("billing.invoice_prepared", invoice, cancellationToken);
        return invoice;
    }

    public async Task<Invoice> IssueInvoiceAsync(Guid invoiceId, CancellationToken cancellationToken)
    {
        var now = _clock.UtcNow;
        var invoice = await RequireAsync(invoiceId, cancellationToken);
        var before = Snapshot(invoice);

        var number = await _invoices.ReserveNumberAsync(now.Year, cancellationToken);
        invoice.Issue(number, now, now.Add(_options.PaymentTerms));
        await _invoices.SaveChangesAsync(cancellationToken);

        await AuditInvoiceAsync("billing.invoice_issued", invoice, cancellationToken, before);
        return invoice;
    }

    public async Task<Invoice> RecordPaymentAsync(Guid invoiceId, RecordPaymentInput input, CancellationToken cancellationToken)
    {
        var invoice = await RequireAsync(invoiceId, cancellationToken);
        var before = Snapshot(invoice);

        var payment = invoice.RecordPayment(
            Money.Of(input.Amount, input.Currency),
            input.Method,
            input.Reference,
            _actor.ActorUserId,
            input.PaidAt);

        _invoices.RegisterNewPayment(payment);
        await _invoices.SaveChangesAsync(cancellationToken);

        await AuditInvoiceAsync("billing.payment_recorded", invoice, cancellationToken, before);
        return invoice;
    }

    public async Task<Invoice> VoidInvoiceAsync(Guid invoiceId, CancellationToken cancellationToken)
    {
        var invoice = await RequireAsync(invoiceId, cancellationToken);
        var before = Snapshot(invoice);

        invoice.Void(_clock.UtcNow);
        await _invoices.SaveChangesAsync(cancellationToken);

        await AuditInvoiceAsync("billing.invoice_voided", invoice, cancellationToken, before);
        return invoice;
    }

    /// <summary>
    /// Applies the tax rate configured for the invoice's currency, if any, to a
    /// freshly-rebuilt draft. The rate is set per currency by hand
    /// (<see cref="BillingOptions.TaxRates"/>) because KNIGHT does not derive tax
    /// from a jurisdiction; here it only multiplies the subtotal by that rate.
    /// A currency with no configured rate is left tax-free, as before.
    /// </summary>
    private void ApplyConfiguredTax(Invoice invoice, DateTimeOffset now)
    {
        if (!_options.TaxRates.TryGetValue(invoice.Currency, out var rate) || rate <= 0m)
        {
            return;
        }

        invoice.SetTax(Money.Of(invoice.Subtotal * rate, invoice.Currency), now);
    }

    private async Task<Invoice> RequireAsync(Guid id, CancellationToken cancellationToken) =>
        await _invoices.GetByIdAsync(id, cancellationToken)
        ?? throw new NotFoundException($"Invoice '{id}' was not found.");

    private Task AuditAccountAsync(string action, BillingAccount account, CancellationToken cancellationToken) =>
        _audit.RecordAsync(
            action,
            nameof(BillingAccount),
            account.Id.ToString(),
            account.CustomerId,
            cancellationToken,
            newValue: new { account.Currency, account.BillingEmail, account.TaxId });

    private Task AuditInvoiceAsync(string action, Invoice invoice, CancellationToken cancellationToken, object? before = null) =>
        _audit.RecordAsync(
            action,
            nameof(Invoice),
            invoice.Id.ToString(),
            invoice.CustomerId,
            cancellationToken,
            before,
            Snapshot(invoice));

    private static object Snapshot(Invoice invoice) => new
    {
        invoice.Number,
        Status = invoice.Status.ToString(),
        invoice.Currency,
        invoice.Subtotal,
        invoice.Tax,
        invoice.Total,
        invoice.PeriodStart,
        invoice.PeriodEnd,
        Paid = invoice.PaidAmount,
        Outstanding = invoice.OutstandingAmount,
        LineCount = invoice.Lines.Count,
    };

    public async Task<BillingRunResult> RunAsync(CancellationToken cancellationToken)
    {
        var now = _clock.UtcNow;
        var due = await _subscriptions.ListDueForBillingAsync(now, cancellationToken);

        var considered = 0;
        var invoiced = 0;
        var issued = 0;
        var failed = 0;

        foreach (var subscription in due.Take(_options.RunBatchSize))
        {
            considered++;

            try
            {
                // Order matters and is the whole correctness argument. The
                // invoice for the closed period is written first; only then does
                // the period roll. A run interrupted between the two leaves a
                // period that will be picked up and billed again — which the
                // rebuild-in-place draft makes harmless — rather than a period
                // that was never billed and never will be.
                var invoice = await PrepareInvoiceAsync(subscription.SubscriptionId, cancellationToken);
                invoiced++;

                if (_options.IssueAutomatically)
                {
                    await IssueInvoiceAsync(invoice.Id, cancellationToken);
                    issued++;
                }

                await _periods.AdvancePeriodAsync(
                    subscription.SubscriptionId,
                    subscription.CurrentPeriodEnd.Add(_options.BillingPeriod),
                    cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // One customer's bad data must not stop everybody else being
                // billed. Recorded as an audit entry rather than only a log line,
                // because "why was this customer not invoiced last month" is a
                // question asked weeks later.
                failed++;

                await _audit.RecordAsync(
                    "billing.run_failed",
                    "Subscription",
                    subscription.SubscriptionId.ToString(),
                    subscription.CustomerId,
                    cancellationToken,
                    newValue: new { Error = exception.Message, subscription.CurrentPeriodEnd });
            }
        }

        if (considered > 0)
        {
            await _audit.RecordAsync(
                "billing.run_completed",
                "BillingRun",
                null,
                null,
                cancellationToken,
                newValue: new { Considered = considered, Invoiced = invoiced, Issued = issued, Failed = failed });
        }

        return new BillingRunResult(considered, invoiced, issued, failed);
    }
}
