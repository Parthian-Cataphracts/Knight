using Knight.Application.Exceptions;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Stores.Domain;

namespace Knight.Infrastructure.ControlPlane.Repositories;

/// <summary>
/// Control-plane stores. Credentials are always loaded with their store: they
/// are part of the aggregate and are only ever mutated through it.
/// </summary>
internal sealed class ControlPlaneStoreRepository : IStoreRepository
{
    private const string PostgresUniqueViolationSqlState = "23505";

    private readonly ControlPlaneDbContext _context;

    public ControlPlaneStoreRepository(ControlPlaneDbContext context)
    {
        _context = context;
    }

    public Task<Store?> GetByIdAsync(Guid id, CancellationToken cancellationToken) =>
        _context.Stores.Include(s => s.Credentials).FirstOrDefaultAsync(s => s.Id == id, cancellationToken);

    public Task<Store?> GetBySlugAsync(string normalizedSlug, CancellationToken cancellationToken) =>
        _context.Stores.Include(s => s.Credentials).FirstOrDefaultAsync(s => s.Slug == normalizedSlug, cancellationToken);

    public Task<Store?> GetByPrimaryDomainAsync(string normalizedHost, CancellationToken cancellationToken) =>
        _context.Stores.Include(s => s.Credentials).FirstOrDefaultAsync(s => s.PrimaryDomain == normalizedHost, cancellationToken);

    /// <summary>
    /// Deliberately unfiltered. A store presenting a credential has no customer
    /// scope yet — proving the credential is what establishes one — so the
    /// isolation filter would reject the very lookup that makes isolation
    /// possible, exactly as it would for a login by email. Nothing is returned to
    /// the caller until the secret has been verified against the row this finds.
    /// </summary>
    public async Task<Store?> GetByClientIdAsync(string clientId, CancellationToken cancellationToken)
    {
        var storeId = await _context.StoreCredentials
            .IgnoreQueryFilters()
            .Where(c => c.ClientId == clientId)
            .Select(c => c.StoreId)
            .FirstOrDefaultAsync(cancellationToken);

        return storeId == Guid.Empty
            ? null
            : await _context.Stores
                .IgnoreQueryFilters()
                .Include(s => s.Credentials)
                .FirstOrDefaultAsync(s => s.Id == storeId, cancellationToken);
    }

    public async Task<IReadOnlyCollection<Store>> ListForHealthPollingAsync(int limit, CancellationToken cancellationToken) =>
        await _context.Stores
            .Where(s => s.IntegrationStatus != IntegrationStatus.NotRegistered && s.Status != StoreStatus.Archived)

            // Never heard from first, then longest since last contact. EF's
            // ordering puts nulls last on Postgres, so the presence of a value is
            // sorted on explicitly rather than left to the provider.
            .OrderBy(s => s.LastSeenAt.HasValue)
            .ThenBy(s => s.LastSeenAt)
            .Take(limit)
            .ToArrayAsync(cancellationToken);

    public async Task<(IReadOnlyCollection<Store> Items, long TotalCount)> ListAsync(
        int page,
        int pageSize,
        Guid? customerId,
        StoreEnvironment? environment,
        StoreStatus? status,
        CancellationToken cancellationToken)
    {
        var query = _context.Stores.Include(s => s.Credentials).AsQueryable();

        if (customerId is not null)
        {
            query = query.Where(s => s.CustomerId == customerId);
        }

        if (environment is not null)
        {
            query = query.Where(s => s.Environment == environment);
        }

        if (status is not null)
        {
            query = query.Where(s => s.Status == status);
        }

        var ordered = query.OrderByDescending(s => s.CreatedAt).ThenBy(s => s.Id);

        var totalCount = await ordered.LongCountAsync(cancellationToken);
        var items = await ordered.Skip((page - 1) * pageSize).Take(pageSize).ToArrayAsync(cancellationToken);

        return (items, totalCount);
    }

    public async Task AddAsync(Store store, CancellationToken cancellationToken) =>
        await _context.Stores.AddAsync(store, cancellationToken);

    public void RegisterNewCredential(StoreCredential credential) =>
        // Forced to Added rather than left to graph traversal: a new child hanging
        // off a tracked parent's collection is classified from whether its key is
        // set, and these keys are client-generated, so EF would infer Modified and
        // issue an UPDATE against a row that does not exist yet.
        _context.Entry(credential).State = EntityState.Added;

    public async Task SaveChangesAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            throw new UniqueConstraintViolationException("The store conflicts with an existing one.", ex);
        }
    }

    private static bool IsUniqueViolation(DbUpdateException exception) =>
        exception.InnerException is PostgresException { SqlState: PostgresUniqueViolationSqlState };
}
