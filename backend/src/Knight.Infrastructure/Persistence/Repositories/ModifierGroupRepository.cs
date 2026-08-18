using Catalog.Domain;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Knight.Application.Exceptions;

namespace Knight.Infrastructure.Persistence.Repositories;

internal sealed class ModifierGroupRepository : IModifierGroupRepository
{
    private const string PostgresUniqueViolationSqlState = "23505";

    private readonly PlatformDbContext _context;

    public ModifierGroupRepository(PlatformDbContext context)
    {
        _context = context;
    }

    public Task<ModifierGroup?> GetByIdAsync(Guid tenantId, Guid modifierGroupId, CancellationToken cancellationToken) =>
        _context.ModifierGroups.FirstOrDefaultAsync(g => g.TenantId == tenantId && g.Id == modifierGroupId, cancellationToken);

    public async Task<(IReadOnlyCollection<ModifierGroup> Items, long TotalCount)> ListAsync(Guid tenantId, int page, int pageSize, string? search, CancellationToken cancellationToken)
    {
        var query = _context.ModifierGroups.Where(g => g.TenantId == tenantId);

        if (!string.IsNullOrWhiteSpace(search))
        {
            var pattern = $"%{search.Trim()}%";
            query = query.Where(g => EF.Functions.ILike(g.Name, pattern));
        }

        var totalCount = await query.LongCountAsync(cancellationToken);

        var items = await query
            .OrderBy(g => g.DisplayOrder)
            .ThenBy(g => g.CreatedAt)
            .ThenBy(g => g.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToArrayAsync(cancellationToken);

        return (items, totalCount);
    }

    public async Task AddAsync(ModifierGroup modifierGroup, CancellationToken cancellationToken)
    {
        await _context.ModifierGroups.AddAsync(modifierGroup, cancellationToken);
        await SaveChangesAsync(cancellationToken);
    }

    public async Task DeleteAsync(ModifierGroup modifierGroup, CancellationToken cancellationToken)
    {
        _context.ModifierGroups.Remove(modifierGroup);
        await _context.SaveChangesAsync(cancellationToken);
    }

    public async Task SaveChangesAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            throw new UniqueConstraintViolationException("A unique constraint was violated while saving modifier group data.", ex);
        }
    }

    public Task<bool> HasAssignmentsAsync(Guid tenantId, Guid modifierGroupId, CancellationToken cancellationToken) =>
        _context.ProductModifierGroups.AnyAsync(
            a => a.TenantId == tenantId && a.ModifierGroupId == modifierGroupId,
            cancellationToken);

    private static bool IsUniqueViolation(DbUpdateException ex) =>
        ex.InnerException is PostgresException { SqlState: PostgresUniqueViolationSqlState };
}
