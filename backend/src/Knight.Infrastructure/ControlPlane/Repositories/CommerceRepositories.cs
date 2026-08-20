using Billing.Domain;
using FeatureRegistry.Domain;
using Knight.Application.Exceptions;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Plans.Domain;
using Subscriptions.Domain;

namespace Knight.Infrastructure.ControlPlane.Repositories;

/// <summary>
/// The feature catalogue is platform-owned: every customer sees the same list, so
/// none of these queries is customer filtered. What differs per customer is which
/// features they are entitled to, and that lives in another table entirely.
/// </summary>
internal sealed class FeatureRepository : IFeatureRepository
{
    private const string PostgresUniqueViolationSqlState = "23505";

    private readonly ControlPlaneDbContext _context;

    public FeatureRepository(ControlPlaneDbContext context)
    {
        _context = context;
    }

    public Task<Feature?> GetByIdAsync(Guid id, CancellationToken cancellationToken) =>
        _context.Features.FirstOrDefaultAsync(f => f.Id == id, cancellationToken);

    public Task<Feature?> GetBySlugAsync(string normalizedSlug, CancellationToken cancellationToken) =>
        _context.Features.FirstOrDefaultAsync(f => f.Slug == normalizedSlug, cancellationToken);

    public async Task<IReadOnlyCollection<Feature>> GetManyAsync(
        IReadOnlyCollection<Guid> ids,
        CancellationToken cancellationToken)
    {
        if (ids.Count == 0)
        {
            return [];
        }

        return await _context.Features.Where(f => ids.Contains(f.Id)).ToArrayAsync(cancellationToken);
    }

    public async Task<(IReadOnlyCollection<Feature> Items, long TotalCount)> ListAsync(
        int page,
        int pageSize,
        FeatureStatus? status,
        string? category,
        string? search,
        CancellationToken cancellationToken)
    {
        var query = _context.Features.AsQueryable();

        if (status is not null)
        {
            query = query.Where(f => f.Status == status);
        }

        if (!string.IsNullOrWhiteSpace(category))
        {
            query = query.Where(f => f.Category == category);
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = $"%{search.Trim()}%";
            query = query.Where(f => EF.Functions.ILike(f.Name, term) || EF.Functions.ILike(f.Slug, term));
        }

        var ordered = query.OrderBy(f => f.Category).ThenBy(f => f.Name).ThenBy(f => f.Id);

        var totalCount = await ordered.LongCountAsync(cancellationToken);
        var items = await ordered.Skip((page - 1) * pageSize).Take(pageSize).ToArrayAsync(cancellationToken);

        return (items, totalCount);
    }

    public async Task AddAsync(Feature feature, CancellationToken cancellationToken) =>
        await _context.Features.AddAsync(feature, cancellationToken);

    public async Task SaveChangesAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresUniqueViolationSqlState })
        {
            throw new UniqueConstraintViolationException("The feature conflicts with an existing one.", ex);
        }
    }
}

internal sealed class PlanRepository : IPlanRepository
{
    private const string PostgresUniqueViolationSqlState = "23505";

    private readonly ControlPlaneDbContext _context;

    public PlanRepository(ControlPlaneDbContext context)
    {
        _context = context;
    }

    public Task<Plan?> GetByIdAsync(Guid id, CancellationToken cancellationToken) =>
        _context.Plans.Include(p => p.Features).FirstOrDefaultAsync(p => p.Id == id, cancellationToken);

    public Task<Plan?> GetByKeyAsync(string key, CancellationToken cancellationToken) =>
        _context.Plans.Include(p => p.Features).FirstOrDefaultAsync(p => p.Key == key, cancellationToken);

    public async Task<IReadOnlyCollection<Plan>> ListAsync(bool includeInactive, CancellationToken cancellationToken)
    {
        var query = _context.Plans.Include(p => p.Features).AsQueryable();

        if (!includeInactive)
        {
            query = query.Where(p => p.IsActive);
        }

        return await query.OrderBy(p => p.SortOrder).ThenBy(p => p.Key).ToArrayAsync(cancellationToken);
    }

    public async Task AddAsync(Plan plan, CancellationToken cancellationToken) =>
        await _context.Plans.AddAsync(plan, cancellationToken);

    public void RegisterNewFeature(PlanFeature feature) =>
        _context.Entry(feature).State = EntityState.Added;

    public void RemoveFeature(PlanFeature feature) =>
        _context.Entry(feature).State = EntityState.Deleted;

    public async Task SaveChangesAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresUniqueViolationSqlState })
        {
            throw new UniqueConstraintViolationException("The plan conflicts with an existing one.", ex);
        }
    }
}

internal sealed class FeaturePriceRepository : IFeaturePriceRepository
{
    private readonly ControlPlaneDbContext _context;

    public FeaturePriceRepository(ControlPlaneDbContext context)
    {
        _context = context;
    }

    /// <summary>
    /// Every price in force at the moment being priced, for this plan or for no
    /// plan in particular. Choosing between them is the calculator's job — the
    /// repository does not decide what something costs.
    /// </summary>
    public async Task<IReadOnlyCollection<FeaturePrice>> GetApplicableAsync(
        IReadOnlyCollection<Guid> featureIds,
        Guid planId,
        DateTimeOffset moment,
        CancellationToken cancellationToken)
    {
        if (featureIds.Count == 0)
        {
            return [];
        }

        return await _context.FeaturePrices
            .Where(price =>
                featureIds.Contains(price.FeatureId) &&
                (price.PlanId == null || price.PlanId == planId) &&
                price.ValidFrom <= moment &&
                (price.ValidTo == null || price.ValidTo > moment))
            .ToArrayAsync(cancellationToken);
    }

    public async Task<IReadOnlyCollection<FeaturePrice>> ListForFeatureAsync(Guid featureId, CancellationToken cancellationToken) =>
        await _context.FeaturePrices
            .Where(price => price.FeatureId == featureId)
            .OrderByDescending(price => price.ValidFrom)
            .ToArrayAsync(cancellationToken);

    public async Task AddAsync(FeaturePrice price, CancellationToken cancellationToken) =>
        await _context.FeaturePrices.AddAsync(price, cancellationToken);

    public Task SaveChangesAsync(CancellationToken cancellationToken) => _context.SaveChangesAsync(cancellationToken);
}

internal sealed class SubscriptionRepository : ISubscriptionRepository
{
    private readonly ControlPlaneDbContext _context;

    public SubscriptionRepository(ControlPlaneDbContext context)
    {
        _context = context;
    }

    public Task<Subscription?> GetByIdAsync(Guid id, CancellationToken cancellationToken) =>
        _context.Subscriptions.Include(s => s.Features).FirstOrDefaultAsync(s => s.Id == id, cancellationToken);

    public Task<Subscription?> GetActiveForCustomerAsync(Guid customerId, CancellationToken cancellationToken) =>
        _context.Subscriptions
            .Include(s => s.Features)
            .Where(s => s.CustomerId == customerId && s.Status != SubscriptionStatus.Cancelled)
            .OrderByDescending(s => s.StartedAt)
            .FirstOrDefaultAsync(cancellationToken);

    public async Task<(IReadOnlyCollection<Subscription> Items, long TotalCount)> ListAsync(
        int page,
        int pageSize,
        Guid? customerId,
        SubscriptionStatus? status,
        CancellationToken cancellationToken)
    {
        var query = _context.Subscriptions.Include(s => s.Features).AsQueryable();

        if (customerId is not null)
        {
            query = query.Where(s => s.CustomerId == customerId);
        }

        if (status is not null)
        {
            query = query.Where(s => s.Status == status);
        }

        var ordered = query.OrderByDescending(s => s.StartedAt).ThenBy(s => s.Id);

        var totalCount = await ordered.LongCountAsync(cancellationToken);
        var items = await ordered.Skip((page - 1) * pageSize).Take(pageSize).ToArrayAsync(cancellationToken);

        return (items, totalCount);
    }

    /// <summary>
    /// Active subscriptions whose period has closed.
    ///
    /// <c>IgnoreQueryFilters</c> because the billing run is platform work across
    /// every customer, and the isolation filter would otherwise fail closed and
    /// return nothing — which looks exactly like there being nothing to bill.
    /// The caller is a background service with a platform scope, never a request.
    /// </summary>
    public async Task<IReadOnlyCollection<Subscription>> ListDueForBillingAsync(
        DateTimeOffset asOf,
        CancellationToken cancellationToken) =>
        await _context.Subscriptions
            .IgnoreQueryFilters()
            .Include(s => s.Features)
            .Where(s => s.Status == SubscriptionStatus.Active && s.CurrentPeriodEnd <= asOf)
            .OrderBy(s => s.CurrentPeriodEnd)
            .ThenBy(s => s.Id)
            .ToListAsync(cancellationToken);

    public async Task AddAsync(Subscription subscription, CancellationToken cancellationToken) =>
        await _context.Subscriptions.AddAsync(subscription, cancellationToken);

    public void RegisterNewFeature(SubscriptionFeature feature) =>
        _context.Entry(feature).State = EntityState.Added;

    public void RemoveFeature(SubscriptionFeature feature) =>
        _context.Entry(feature).State = EntityState.Deleted;

    public Task SaveChangesAsync(CancellationToken cancellationToken) => _context.SaveChangesAsync(cancellationToken);
}

internal sealed class FeatureEntitlementRepository : IFeatureEntitlementRepository
{
    private readonly ControlPlaneDbContext _context;

    public FeatureEntitlementRepository(ControlPlaneDbContext context)
    {
        _context = context;
    }

    public async Task<IReadOnlyCollection<FeatureEntitlement>> ListForCustomerAsync(
        Guid customerId,
        bool includeInactive,
        CancellationToken cancellationToken)
    {
        var query = _context.FeatureEntitlements.Where(e => e.CustomerId == customerId);

        if (!includeInactive)
        {
            query = query.Where(e => e.RevokedAt == null);
        }

        return await query.OrderBy(e => e.GrantedAt).ToArrayAsync(cancellationToken);
    }

    public Task<FeatureEntitlement?> FindActiveAsync(
        Guid customerId,
        Guid featureId,
        DateTimeOffset moment,
        CancellationToken cancellationToken) =>
        _context.FeatureEntitlements
            .Where(e =>
                e.CustomerId == customerId &&
                e.FeatureId == featureId &&
                e.RevokedAt == null &&
                e.GrantedAt <= moment &&
                (e.ExpiresAt == null || e.ExpiresAt > moment))
            .OrderByDescending(e => e.GrantedAt)
            .FirstOrDefaultAsync(cancellationToken);

    public async Task AddAsync(FeatureEntitlement entitlement, CancellationToken cancellationToken) =>
        await _context.FeatureEntitlements.AddAsync(entitlement, cancellationToken);

    public Task SaveChangesAsync(CancellationToken cancellationToken) => _context.SaveChangesAsync(cancellationToken);
}

internal sealed class BillingAccountRepository : IBillingAccountRepository
{
    private readonly ControlPlaneDbContext _context;

    public BillingAccountRepository(ControlPlaneDbContext context)
    {
        _context = context;
    }

    public Task<BillingAccount?> GetForCustomerAsync(Guid customerId, CancellationToken cancellationToken) =>
        _context.BillingAccounts.FirstOrDefaultAsync(a => a.CustomerId == customerId, cancellationToken);

    public async Task AddAsync(BillingAccount account, CancellationToken cancellationToken) =>
        await _context.BillingAccounts.AddAsync(account, cancellationToken);

    public Task SaveChangesAsync(CancellationToken cancellationToken) => _context.SaveChangesAsync(cancellationToken);
}

internal sealed class InvoiceRepository : IInvoiceRepository
{
    private readonly ControlPlaneDbContext _context;

    public InvoiceRepository(ControlPlaneDbContext context)
    {
        _context = context;
    }

    public Task<Invoice?> GetByIdAsync(Guid id, CancellationToken cancellationToken) =>
        _context.Invoices
            .Include(i => i.Lines)
            .Include(i => i.Payments)
            .FirstOrDefaultAsync(i => i.Id == id, cancellationToken);

    public Task<Invoice?> GetDraftForPeriodAsync(
        Guid customerId,
        DateTimeOffset periodStart,
        DateTimeOffset periodEnd,
        CancellationToken cancellationToken) =>
        _context.Invoices
            .Include(i => i.Lines)
            .Include(i => i.Payments)
            .FirstOrDefaultAsync(
                i => i.CustomerId == customerId &&
                     i.Status == InvoiceStatus.Draft &&
                     i.PeriodStart == periodStart &&
                     i.PeriodEnd == periodEnd,
                cancellationToken);

    public async Task<(IReadOnlyCollection<Invoice> Items, long TotalCount)> ListAsync(
        int page,
        int pageSize,
        Guid? customerId,
        InvoiceStatus? status,
        CancellationToken cancellationToken)
    {
        var query = _context.Invoices.Include(i => i.Lines).Include(i => i.Payments).AsQueryable();

        if (customerId is not null)
        {
            query = query.Where(i => i.CustomerId == customerId);
        }

        if (status is not null)
        {
            query = query.Where(i => i.Status == status);
        }

        var ordered = query.OrderByDescending(i => i.PeriodStart).ThenBy(i => i.Id);

        var totalCount = await ordered.LongCountAsync(cancellationToken);
        var items = await ordered.Skip((page - 1) * pageSize).Take(pageSize).ToArrayAsync(cancellationToken);

        return (items, totalCount);
    }

    public async Task AddAsync(Invoice invoice, CancellationToken cancellationToken) =>
        await _context.Invoices.AddAsync(invoice, cancellationToken);

    public void RegisterNewLine(InvoiceLine line) => _context.Entry(line).State = EntityState.Added;

    public void RemoveLine(InvoiceLine line) => _context.Entry(line).State = EntityState.Deleted;

    public void RegisterNewPayment(PaymentRecord payment) => _context.Entry(payment).State = EntityState.Added;

    /// <summary>
    /// Reserves the next number by incrementing the counter row in a single
    /// atomic statement. Two callers issuing at the same instant are serialised
    /// by the row lock, so neither can read a value the other is about to take —
    /// which a read-then-write in application code could not guarantee.
    /// </summary>
    public async Task<string> ReserveNumberAsync(int year, CancellationToken cancellationToken)
    {
        var reserved = await _context.Database
            .SqlQuery<int>($"""
                INSERT INTO control.invoice_number_sequences ("Year", "LastNumber")
                VALUES ({year}, 1)
                ON CONFLICT ("Year")
                DO UPDATE SET "LastNumber" = control.invoice_number_sequences."LastNumber" + 1
                RETURNING "LastNumber"
                """)
            .ToArrayAsync(cancellationToken);

        return InvoiceNumberSequence.Format(year, reserved.Single());
    }

    public Task SaveChangesAsync(CancellationToken cancellationToken) => _context.SaveChangesAsync(cancellationToken);
}
