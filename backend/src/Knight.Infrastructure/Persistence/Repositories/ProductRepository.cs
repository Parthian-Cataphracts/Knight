using Catalog.Domain;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Knight.Application.Exceptions;

namespace Knight.Infrastructure.Persistence.Repositories;

internal sealed class ProductRepository : IProductRepository
{
    private const string PostgresUniqueViolationSqlState = "23505";

    private readonly PlatformDbContext _context;

    public ProductRepository(PlatformDbContext context)
    {
        _context = context;
    }

    public Task<Product?> GetByIdAsync(Guid tenantId, Guid productId, CancellationToken cancellationToken) =>
        _context.Products.FirstOrDefaultAsync(p => p.TenantId == tenantId && p.Id == productId, cancellationToken);

    public Task<Product?> GetBySlugAsync(Guid tenantId, string normalizedSlug, CancellationToken cancellationToken) =>
        _context.Products.FirstOrDefaultAsync(p => p.TenantId == tenantId && p.Slug == normalizedSlug, cancellationToken);

    public async Task<(IReadOnlyCollection<Product> Items, long TotalCount)> ListAsync(
        Guid tenantId,
        int page,
        int pageSize,
        Guid? categoryId,
        ProductStatus? status,
        bool? isVisible,
        bool? isAvailable,
        string? search,
        CancellationToken cancellationToken)
    {
        var query = _context.Products.Where(p => p.TenantId == tenantId);

        if (categoryId is not null)
        {
            query = query.Where(p => p.CategoryId == categoryId.Value);
        }

        if (status is not null)
        {
            query = query.Where(p => p.Status == status.Value);
        }

        if (isVisible is not null)
        {
            query = query.Where(p => p.IsVisible == isVisible.Value);
        }

        if (isAvailable is not null)
        {
            query = query.Where(p => p.IsAvailable == isAvailable.Value);
        }

        query = ApplySearch(query, search);

        return await PageAsync(query, page, pageSize, cancellationToken);
    }

    public async Task<(IReadOnlyCollection<Product> Items, long TotalCount)> ListPublicAsync(
        Guid tenantId,
        int page,
        int pageSize,
        Guid? categoryId,
        string? search,
        CancellationToken cancellationToken)
    {
        // Only Active products are storefront-visible: Draft is unpublished work in
        // progress and Archived is withdrawn, so neither may ever surface publicly
        // regardless of the visibility flag.
        var query = _context.Products
            .Where(p => p.TenantId == tenantId && p.IsVisible && p.Status == ProductStatus.Active);

        if (categoryId is not null)
        {
            query = query.Where(p => p.CategoryId == categoryId.Value);
        }

        query = ApplySearch(query, search);

        return await PageAsync(query, page, pageSize, cancellationToken);
    }

    public async Task<ProductDetail?> GetPublicBySlugAsync(Guid tenantId, string normalizedSlug, CancellationToken cancellationToken)
    {
        var product = await _context.Products
            .AsNoTracking()
            .FirstOrDefaultAsync(
                // Same storefront rule as ListPublicAsync: Active only.
                p => p.TenantId == tenantId
                    && p.Slug == normalizedSlug
                    && p.IsVisible
                    && p.Status == ProductStatus.Active,
                cancellationToken);

        if (product is null)
        {
            return null;
        }

        var variants = await _context.ProductVariants
            .AsNoTracking()
            .Where(v => v.TenantId == tenantId && v.ProductId == product.Id)
            .OrderBy(v => v.DisplayOrder)
            .ThenBy(v => v.CreatedAt)
            .ThenBy(v => v.Id)
            .ToArrayAsync(cancellationToken);

        var media = await _context.ProductMedia
            .AsNoTracking()
            .Where(m => m.TenantId == tenantId && m.ProductId == product.Id)
            .OrderBy(m => m.DisplayOrder)
            .ThenBy(m => m.CreatedAt)
            .ThenBy(m => m.Id)
            .ToArrayAsync(cancellationToken);

        // One join query for the assigned groups, one for their modifiers: bounded
        // round trips regardless of how many groups a product carries.
        var assignedGroups = await (
            from assignment in _context.ProductModifierGroups.AsNoTracking()
            join grp in _context.ModifierGroups.AsNoTracking()
                on new { assignment.TenantId, GroupId = assignment.ModifierGroupId } equals new { grp.TenantId, GroupId = grp.Id }
            where assignment.TenantId == tenantId && assignment.ProductId == product.Id
            orderby assignment.DisplayOrder, grp.Name, grp.Id
            select new { Group = grp, assignment.DisplayOrder })
            .ToArrayAsync(cancellationToken);

        var groupIds = assignedGroups.Select(g => g.Group.Id).ToArray();

        Modifier[] modifiers = groupIds.Length == 0
            ? []
            : await _context.Modifiers
                .AsNoTracking()
                .Where(m => m.TenantId == tenantId && groupIds.Contains(m.ModifierGroupId))
                .OrderBy(m => m.DisplayOrder)
                .ThenBy(m => m.CreatedAt)
                .ThenBy(m => m.Id)
                .ToArrayAsync(cancellationToken);

        var modifiersByGroup = modifiers
            .GroupBy(m => m.ModifierGroupId)
            .ToDictionary(g => g.Key, g => (IReadOnlyCollection<Modifier>)g.ToArray());

        var modifierGroups = assignedGroups
            .Select(g => new ProductModifierGroupDetail
            {
                Group = g.Group,
                DisplayOrder = g.DisplayOrder,
                Modifiers = modifiersByGroup.TryGetValue(g.Group.Id, out var groupModifiers) ? groupModifiers : []
            })
            .ToArray();

        return new ProductDetail
        {
            Product = product,
            Variants = variants,
            ModifierGroups = modifierGroups,
            Media = media
        };
    }

    public async Task AddAsync(Product product, CancellationToken cancellationToken)
    {
        await _context.Products.AddAsync(product, cancellationToken);
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
            throw new UniqueConstraintViolationException("A unique constraint was violated while saving product data.", ex);
        }
    }

    private static IQueryable<Product> ApplySearch(IQueryable<Product> query, string? search)
    {
        if (string.IsNullOrWhiteSpace(search))
        {
            return query;
        }

        var pattern = $"%{search.Trim()}%";
        return query.Where(p => EF.Functions.ILike(p.Name, pattern) || EF.Functions.ILike(p.Slug, pattern));
    }

    private static async Task<(IReadOnlyCollection<Product> Items, long TotalCount)> PageAsync(
        IQueryable<Product> query,
        int page,
        int pageSize,
        CancellationToken cancellationToken)
    {
        var totalCount = await query.LongCountAsync(cancellationToken);

        var items = await query
            .OrderBy(p => p.DisplayOrder)
            .ThenBy(p => p.CreatedAt)
            .ThenBy(p => p.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToArrayAsync(cancellationToken);

        return (items, totalCount);
    }

    private static bool IsUniqueViolation(DbUpdateException ex) =>
        ex.InnerException is PostgresException { SqlState: PostgresUniqueViolationSqlState };
}
