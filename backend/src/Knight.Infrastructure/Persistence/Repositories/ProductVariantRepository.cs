using Catalog.Domain;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Knight.Application.Exceptions;

namespace Knight.Infrastructure.Persistence.Repositories;

internal sealed class ProductVariantRepository : IProductVariantRepository
{
    private const string PostgresUniqueViolationSqlState = "23505";

    private readonly PlatformDbContext _context;

    public ProductVariantRepository(PlatformDbContext context)
    {
        _context = context;
    }

    public Task<ProductVariant?> GetByIdAsync(Guid tenantId, Guid productId, Guid variantId, CancellationToken cancellationToken) =>
        _context.ProductVariants.FirstOrDefaultAsync(
            v => v.TenantId == tenantId && v.ProductId == productId && v.Id == variantId,
            cancellationToken);

    public async Task<IReadOnlyCollection<ProductVariant>> ListByProductAsync(Guid tenantId, Guid productId, CancellationToken cancellationToken) =>
        await _context.ProductVariants
            .Where(v => v.TenantId == tenantId && v.ProductId == productId)
            .OrderBy(v => v.DisplayOrder)
            .ThenBy(v => v.CreatedAt)
            .ThenBy(v => v.Id)
            .ToArrayAsync(cancellationToken);

    public async Task AddAsync(ProductVariant variant, CancellationToken cancellationToken)
    {
        await _context.ProductVariants.AddAsync(variant, cancellationToken);
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
            throw new UniqueConstraintViolationException("A unique constraint was violated while saving product variant data.", ex);
        }
    }

    public async Task<bool> SetDefaultAsync(Guid tenantId, Guid productId, Guid variantId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var transaction = await _context.Database.BeginTransactionAsync(cancellationToken);

        var exists = await _context.ProductVariants
            .AnyAsync(v => v.TenantId == tenantId && v.ProductId == productId && v.Id == variantId, cancellationToken);

        if (!exists)
        {
            await transaction.RollbackAsync(cancellationToken);
            return false;
        }

        // Clear first, then set: the partial unique index would reject any window in
        // which two rows for the same product carry the default flag.
        await _context.ProductVariants
            .Where(v => v.TenantId == tenantId && v.ProductId == productId && v.IsDefault && v.Id != variantId)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(v => v.IsDefault, false)
                    .SetProperty(v => v.UpdatedAt, (DateTimeOffset?)now),
                cancellationToken);

        await _context.ProductVariants
            .Where(v => v.TenantId == tenantId && v.ProductId == productId && v.Id == variantId)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(v => v.IsDefault, true)
                    .SetProperty(v => v.UpdatedAt, (DateTimeOffset?)now),
                cancellationToken);

        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    private static bool IsUniqueViolation(DbUpdateException ex) =>
        ex.InnerException is PostgresException { SqlState: PostgresUniqueViolationSqlState };
}
