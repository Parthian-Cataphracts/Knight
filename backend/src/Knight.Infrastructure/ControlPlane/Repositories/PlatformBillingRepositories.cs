using Microsoft.EntityFrameworkCore;
using PlatformBilling.Domain;

namespace Knight.Infrastructure.ControlPlane.Repositories;

/// <summary>
/// Persistence for KNIGHT's own billing transactions.
///
/// The lookups a webhook uses — by idempotency key and by provider id —
/// <c>IgnoreQueryFilters</c> on purpose: a provider callback carries no customer
/// scope, and the isolation filter would otherwise fail closed and hide the very
/// row the callback needs to settle. Reads that serve a customer request go
/// through the filter like everything else.
/// </summary>
internal sealed class PlatformBillingTransactionRepository : IPlatformBillingTransactionRepository
{
    private readonly ControlPlaneDbContext _context;

    public PlatformBillingTransactionRepository(ControlPlaneDbContext context)
    {
        _context = context;
    }

    public Task<PlatformBillingTransaction?> GetByIdAsync(Guid id, CancellationToken cancellationToken) =>
        _context.PlatformBillingTransactions.FirstOrDefaultAsync(t => t.Id == id, cancellationToken);

    public Task<PlatformBillingTransaction?> FindByIdempotencyKeyAsync(string idempotencyKey, CancellationToken cancellationToken) =>
        _context.PlatformBillingTransactions
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(t => t.IdempotencyKey == idempotencyKey, cancellationToken);

    public Task<PlatformBillingTransaction?> FindByProviderTransactionAsync(
        string provider,
        string providerTransactionId,
        CancellationToken cancellationToken) =>
        _context.PlatformBillingTransactions
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(
                t => t.Provider == provider && t.ProviderTransactionId == providerTransactionId,
                cancellationToken);

    public async Task AddAsync(PlatformBillingTransaction transaction, CancellationToken cancellationToken) =>
        await _context.PlatformBillingTransactions.AddAsync(transaction, cancellationToken);

    public Task SaveChangesAsync(CancellationToken cancellationToken) =>
        _context.SaveChangesAsync(cancellationToken);
}

/// <summary>
/// Persistence for the activation outbox. The dispatcher reads it in platform
/// scope (it acts for every customer), so the sweep <c>IgnoreQueryFilters</c> —
/// the customer isolation filter would otherwise hide the rows it exists to drain.
/// </summary>
internal sealed class ActivationOutboxRepository : IActivationOutboxRepository
{
    private readonly ControlPlaneDbContext _context;

    public ActivationOutboxRepository(ControlPlaneDbContext context)
    {
        _context = context;
    }

    public async Task AddAsync(ActivationOutboxEntry entry, CancellationToken cancellationToken) =>
        await _context.ActivationOutbox.AddAsync(entry, cancellationToken);

    public async Task<IReadOnlyCollection<ActivationOutboxEntry>> ListDispatchableAsync(
        int limit,
        DateTimeOffset now,
        CancellationToken cancellationToken) =>
        await _context.ActivationOutbox
            .IgnoreQueryFilters()
            .Where(entry => entry.Status == ActivationOutboxStatus.Pending && entry.NextAttemptAt <= now)
            .OrderBy(entry => entry.NextAttemptAt)
            .Take(limit)
            .ToListAsync(cancellationToken);

    public Task SaveChangesAsync(CancellationToken cancellationToken) =>
        _context.SaveChangesAsync(cancellationToken);
}

internal sealed class CheckoutSessionRepository : ICheckoutSessionRepository
{
    private readonly ControlPlaneDbContext _context;

    public CheckoutSessionRepository(ControlPlaneDbContext context)
    {
        _context = context;
    }

    public Task<CheckoutSession?> GetByIdAsync(Guid id, CancellationToken cancellationToken) =>
        _context.CheckoutSessions.FirstOrDefaultAsync(s => s.Id == id, cancellationToken);

    public Task<CheckoutSession?> FindByProviderSessionAsync(
        string provider,
        string providerSessionId,
        CancellationToken cancellationToken) =>
        _context.CheckoutSessions
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(
                s => s.Provider == provider && s.ProviderSessionId == providerSessionId,
                cancellationToken);

    public async Task AddAsync(CheckoutSession session, CancellationToken cancellationToken) =>
        await _context.CheckoutSessions.AddAsync(session, cancellationToken);

    public Task SaveChangesAsync(CancellationToken cancellationToken) =>
        _context.SaveChangesAsync(cancellationToken);
}
