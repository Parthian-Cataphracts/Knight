using Catalog.Domain;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Knight.Application.Exceptions;

namespace Knight.Infrastructure.Persistence.Repositories;

internal sealed class ModifierRepository : IModifierRepository
{
    private const string PostgresUniqueViolationSqlState = "23505";

    private readonly PlatformDbContext _context;

    public ModifierRepository(PlatformDbContext context)
    {
        _context = context;
    }

    public Task<Modifier?> GetByIdAsync(Guid tenantId, Guid modifierGroupId, Guid modifierId, CancellationToken cancellationToken) =>
        _context.Modifiers.FirstOrDefaultAsync(
            m => m.TenantId == tenantId && m.ModifierGroupId == modifierGroupId && m.Id == modifierId,
            cancellationToken);

    public async Task<IReadOnlyCollection<Modifier>> ListByGroupAsync(Guid tenantId, Guid modifierGroupId, CancellationToken cancellationToken) =>
        await _context.Modifiers
            .Where(m => m.TenantId == tenantId && m.ModifierGroupId == modifierGroupId)
            .OrderBy(m => m.DisplayOrder)
            .ThenBy(m => m.CreatedAt)
            .ThenBy(m => m.Id)
            .ToArrayAsync(cancellationToken);

    public async Task AddAsync(Modifier modifier, CancellationToken cancellationToken)
    {
        await _context.Modifiers.AddAsync(modifier, cancellationToken);
        await SaveChangesAsync(cancellationToken);
    }

    public async Task SaveChangesAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            throw new UniqueConstraintViolationException("A unique constraint was violated while saving modifier data.", ex);
        }
    }

    private static bool IsUniqueViolation(DbUpdateException ex) =>
        ex.InnerException is PostgresException { SqlState: PostgresUniqueViolationSqlState };
}
