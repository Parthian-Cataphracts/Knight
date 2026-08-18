using Identity.Domain;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Knight.Application.Exceptions;

namespace Knight.Infrastructure.Persistence.Repositories;

internal sealed class TenantUserRepository : ITenantUserRepository
{
    private const string PostgresUniqueViolationSqlState = "23505";

    private readonly PlatformDbContext _context;

    public TenantUserRepository(PlatformDbContext context)
    {
        _context = context;
    }

    public Task<TenantUser?> GetByIdAsync(Guid tenantId, Guid id, CancellationToken cancellationToken) =>
        _context.TenantUsers.FirstOrDefaultAsync(u => u.TenantId == tenantId && u.Id == id, cancellationToken);

    public Task<TenantUser?> GetByNormalizedEmailAsync(Guid tenantId, string normalizedEmail, CancellationToken cancellationToken) =>
        _context.TenantUsers.FirstOrDefaultAsync(u => u.TenantId == tenantId && u.NormalizedEmail == normalizedEmail, cancellationToken);

    public async Task<(IReadOnlyCollection<TenantUserListItem> Items, long TotalCount)> ListAsync(Guid tenantId, int page, int pageSize, CancellationToken cancellationToken)
    {
        var query = _context.TenantUsers.Where(u => u.TenantId == tenantId).OrderBy(u => u.CreatedAt).ThenBy(u => u.Id);

        var totalCount = await query.LongCountAsync(cancellationToken);
        var users = await query.Skip((page - 1) * pageSize).Take(pageSize).ToArrayAsync(cancellationToken);

        if (users.Length == 0)
        {
            return (Array.Empty<TenantUserListItem>(), totalCount);
        }

        var userIds = users.Select(u => u.Id).ToArray();
        var assignments = await _context.TenantUserRoles
            .Where(a => a.TenantId == tenantId && userIds.Contains(a.TenantUserId))
            .Select(a => new { a.TenantUserId, a.RoleId })
            .ToArrayAsync(cancellationToken);

        var items = users
            .Select(u => new TenantUserListItem
            {
                User = u,
                RoleIds = assignments.Where(a => a.TenantUserId == u.Id).Select(a => a.RoleId).ToArray()
            })
            .ToArray();

        return (items, totalCount);
    }

    public async Task AddAsync(TenantUser user, CancellationToken cancellationToken)
    {
        await _context.TenantUsers.AddAsync(user, cancellationToken);
        await SaveChangesAsync(cancellationToken);
    }

    public async Task AddWithRoleAssignmentsAsync(TenantUser user, IReadOnlyCollection<TenantUserRole> roleAssignments, CancellationToken cancellationToken)
    {
        await using var transaction = await _context.Database.BeginTransactionAsync(cancellationToken);

        try
        {
            await _context.TenantUsers.AddAsync(user, cancellationToken);
            await _context.TenantUserRoles.AddRangeAsync(roleAssignments, cancellationToken);
            await _context.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            await transaction.RollbackAsync(cancellationToken);
            throw new UniqueConstraintViolationException("A unique constraint was violated while creating the staff account.", ex);
        }
    }

    public async Task SaveChangesAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            throw new UniqueConstraintViolationException("A unique constraint was violated while saving tenant user data.", ex);
        }
    }

    private static bool IsUniqueViolation(DbUpdateException ex) =>
        ex.InnerException is PostgresException { SqlState: PostgresUniqueViolationSqlState };
}
