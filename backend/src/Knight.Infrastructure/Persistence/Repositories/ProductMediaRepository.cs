using Catalog.Domain;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Knight.Application.Exceptions;

namespace Knight.Infrastructure.Persistence.Repositories;

internal sealed class ProductMediaRepository : IProductMediaRepository
{
    private const string PostgresUniqueViolationSqlState = "23505";

    private readonly PlatformDbContext _context;

    public ProductMediaRepository(PlatformDbContext context)
    {
        _context = context;
    }

    public Task<ProductMedia?> GetByIdAsync(Guid tenantId, Guid productId, Guid mediaId, CancellationToken cancellationToken) =>
        _context.ProductMedia.FirstOrDefaultAsync(
            m => m.TenantId == tenantId && m.ProductId == productId && m.Id == mediaId,
            cancellationToken);

    public async Task<IReadOnlyCollection<ProductMedia>> ListByProductAsync(Guid tenantId, Guid productId, CancellationToken cancellationToken) =>
        await _context.ProductMedia
            .Where(m => m.TenantId == tenantId && m.ProductId == productId)
            .OrderBy(m => m.DisplayOrder)
            .ThenBy(m => m.CreatedAt)
            .ThenBy(m => m.Id)
            .ToArrayAsync(cancellationToken);

    public async Task AddAsync(ProductMedia media, CancellationToken cancellationToken)
    {
        await _context.ProductMedia.AddAsync(media, cancellationToken);
        await SaveChangesAsync(cancellationToken);
    }

    public async Task DeleteAsync(ProductMedia media, CancellationToken cancellationToken)
    {
        _context.ProductMedia.Remove(media);
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
            throw new UniqueConstraintViolationException("A unique constraint was violated while saving product media data.", ex);
        }
    }

    public async Task<bool> SetPrimaryAsync(Guid tenantId, Guid productId, Guid mediaId, CancellationToken cancellationToken)
    {
        await using var transaction = await _context.Database.BeginTransactionAsync(cancellationToken);

        var exists = await _context.ProductMedia
            .AnyAsync(m => m.TenantId == tenantId && m.ProductId == productId && m.Id == mediaId, cancellationToken);

        if (!exists)
        {
            await transaction.RollbackAsync(cancellationToken);
            return false;
        }

        // Clear first, then set: the partial unique index would reject any window in
        // which two rows for the same product carry the primary flag.
        await _context.ProductMedia
            .Where(m => m.TenantId == tenantId && m.ProductId == productId && m.IsPrimary && m.Id != mediaId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(m => m.IsPrimary, false), cancellationToken);

        await _context.ProductMedia
            .Where(m => m.TenantId == tenantId && m.ProductId == productId && m.Id == mediaId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(m => m.IsPrimary, true), cancellationToken);

        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    private static bool IsUniqueViolation(DbUpdateException ex) =>
        ex.InnerException is PostgresException { SqlState: PostgresUniqueViolationSqlState };
}
