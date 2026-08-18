using Catalog.Domain;

namespace Catalog;

/// <summary>
/// Read-only storefront queries. Anonymous-facing, so it carries no audit or
/// permission concerns and never returns hidden or archived catalog entries.
/// </summary>
public interface ICatalogPublicQueryService
{
    Task<CategoryListResult> ListCategoriesAsync(Guid tenantId, int page, int pageSize, CancellationToken cancellationToken);

    /// <summary>Returns <c>null</c> when the category does not exist or is not visible.</summary>
    Task<Category?> GetCategoryBySlugAsync(Guid tenantId, string slug, CancellationToken cancellationToken);

    Task<ProductListResult> ListProductsAsync(Guid tenantId, int page, int pageSize, Guid? categoryId, string? search, CancellationToken cancellationToken);

    /// <summary>Returns <c>null</c> when the product does not exist, is hidden, or is archived.</summary>
    Task<ProductDetail?> GetProductBySlugAsync(Guid tenantId, string slug, CancellationToken cancellationToken);
}
