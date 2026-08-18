using Catalog.Domain;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Knight.Application.Exceptions;

namespace Knight.Infrastructure.Persistence.Repositories;

internal sealed class CategoryRepository : ICategoryRepository
{
    private const string PostgresUniqueViolationSqlState = "23505";

    private readonly PlatformDbContext _context;

    public CategoryRepository(PlatformDbContext context)
    {
        _context = context;
    }

    public Task<Category?> GetByIdAsync(Guid tenantId, Guid categoryId, CancellationToken cancellationToken) =>
        _context.Categories.FirstOrDefaultAsync(c => c.TenantId == tenantId && c.Id == categoryId, cancellationToken);

    public Task<Category?> GetBySlugAsync(Guid tenantId, string normalizedSlug, CancellationToken cancellationToken) =>
        _context.Categories.FirstOrDefaultAsync(c => c.TenantId == tenantId && c.Slug == normalizedSlug, cancellationToken);

    public async Task<(IReadOnlyCollection<Category> Items, long TotalCount)> ListAsync(Guid tenantId, int page, int pageSize, string? search, bool? isVisible, CancellationToken cancellationToken)
    {
        var query = _context.Categories.Where(c => c.TenantId == tenantId);

        if (!string.IsNullOrWhiteSpace(search))
        {
            var pattern = $"%{search.Trim()}%";
            query = query.Where(c => EF.Functions.ILike(c.Name, pattern) || EF.Functions.ILike(c.Slug, pattern));
        }

        if (isVisible is not null)
        {
            query = query.Where(c => c.IsVisible == isVisible.Value);
        }

        var totalCount = await query.LongCountAsync(cancellationToken);

        var items = await query
            .OrderBy(c => c.DisplayOrder)
            .ThenBy(c => c.CreatedAt)
            .ThenBy(c => c.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToArrayAsync(cancellationToken);

        return (items, totalCount);
    }

    public async Task AddAsync(Category category, CancellationToken cancellationToken)
    {
        await _context.Categories.AddAsync(category, cancellationToken);
        await SaveChangesAsync(cancellationToken);
    }

    public async Task DeleteAsync(Category category, CancellationToken cancellationToken)
    {
        _context.Categories.Remove(category);
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
            throw new UniqueConstraintViolationException("A unique constraint was violated while saving category data.", ex);
        }
    }

    public Task<bool> HasProductsAsync(Guid tenantId, Guid categoryId, CancellationToken cancellationToken) =>
        _context.Products.AnyAsync(p => p.TenantId == tenantId && p.CategoryId == categoryId, cancellationToken);

    private static bool IsUniqueViolation(DbUpdateException ex) =>
        ex.InnerException is PostgresException { SqlState: PostgresUniqueViolationSqlState };
}
