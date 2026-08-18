using Microsoft.EntityFrameworkCore;
using Npgsql;
using Knight.Application.Exceptions;
using Tenancy.Domain;

namespace Knight.Infrastructure.Persistence.Repositories;

internal sealed class TenantRepository : ITenantRepository
{
    private const string PostgresUniqueViolationSqlState = "23505";

    private readonly PlatformDbContext _context;

    public TenantRepository(PlatformDbContext context)
    {
        _context = context;
    }

    public Task<Tenant?> GetByIdAsync(Guid id, CancellationToken cancellationToken) =>
        _context.Tenants.Include(t => t.Domains).FirstOrDefaultAsync(t => t.Id == id, cancellationToken);

    public Task<Tenant?> GetBySlugAsync(string slug, CancellationToken cancellationToken) =>
        _context.Tenants.Include(t => t.Domains).FirstOrDefaultAsync(t => t.Slug == slug, cancellationToken);

    public async Task<Tenant?> GetByDomainHostAsync(string normalizedHost, CancellationToken cancellationToken)
    {
        var tenantId = await _context.TenantDomains
            .Where(d => d.Host == normalizedHost)
            .Select(d => d.TenantId)
            .FirstOrDefaultAsync(cancellationToken);

        return tenantId == Guid.Empty
            ? null
            : await _context.Tenants.Include(t => t.Domains).FirstOrDefaultAsync(t => t.Id == tenantId, cancellationToken);
    }

    public async Task<(IReadOnlyCollection<Tenant> Items, long TotalCount)> ListAsync(int page, int pageSize, CancellationToken cancellationToken)
    {
        var query = _context.Tenants.Include(t => t.Domains).OrderBy(t => t.CreatedAt).ThenBy(t => t.Id);

        var totalCount = await query.LongCountAsync(cancellationToken);
        var items = await query.Skip((page - 1) * pageSize).Take(pageSize).ToArrayAsync(cancellationToken);

        return (items, totalCount);
    }

    public async Task AddAsync(Tenant tenant, CancellationToken cancellationToken)
    {
        await _context.Tenants.AddAsync(tenant, cancellationToken);
        await SaveChangesAsync(cancellationToken);
    }

    public Task RegisterNewDomainAsync(TenantDomain domain, CancellationToken cancellationToken)
    {
        // Force Added rather than relying on graph-traversal inference: a new
        // TenantDomain discovered only via the already-tracked Tenant's Domains
        // navigation was observed to be classified as Modified (generating an
        // UPDATE against a non-existent row) rather than Added. Setting the
        // state explicitly is deterministic regardless of that heuristic.
        _context.Entry(domain).State = EntityState.Added;
        return Task.CompletedTask;
    }

    public async Task SaveChangesAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            throw new UniqueConstraintViolationException("A unique constraint was violated while saving tenant data.", ex);
        }
    }

    private static bool IsUniqueViolation(DbUpdateException ex) =>
        ex.InnerException is PostgresException { SqlState: PostgresUniqueViolationSqlState };
}
