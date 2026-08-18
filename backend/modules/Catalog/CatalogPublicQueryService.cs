using Catalog.Domain;
using Knight.Domain.Exceptions;

namespace Catalog;

public sealed class CatalogPublicQueryService : ICatalogPublicQueryService
{
    private readonly ICategoryRepository _categoryRepository;
    private readonly IProductRepository _productRepository;

    public CatalogPublicQueryService(ICategoryRepository categoryRepository, IProductRepository productRepository)
    {
        _categoryRepository = categoryRepository;
        _productRepository = productRepository;
    }

    public async Task<CategoryListResult> ListCategoriesAsync(Guid tenantId, int page, int pageSize, CancellationToken cancellationToken)
    {
        var (boundedPage, boundedPageSize) = CatalogPaging.Bound(page, pageSize);

        var (items, total) = await _categoryRepository.ListAsync(
            tenantId,
            boundedPage,
            boundedPageSize,
            search: null,
            isVisible: true,
            cancellationToken);

        return new CategoryListResult(items, total, boundedPage, boundedPageSize);
    }

    public async Task<Category?> GetCategoryBySlugAsync(Guid tenantId, string slug, CancellationToken cancellationToken)
    {
        var normalized = TryNormalize(slug);
        if (normalized is null)
        {
            return null;
        }

        var category = await _categoryRepository.GetBySlugAsync(tenantId, normalized, cancellationToken);
        return category is { IsVisible: true } ? category : null;
    }

    public async Task<ProductListResult> ListProductsAsync(Guid tenantId, int page, int pageSize, Guid? categoryId, string? search, CancellationToken cancellationToken)
    {
        var (boundedPage, boundedPageSize) = CatalogPaging.Bound(page, pageSize);

        var (items, total) = await _productRepository.ListPublicAsync(
            tenantId,
            boundedPage,
            boundedPageSize,
            categoryId,
            search,
            cancellationToken);

        return new ProductListResult(items, total, boundedPage, boundedPageSize);
    }

    public async Task<ProductDetail?> GetProductBySlugAsync(Guid tenantId, string slug, CancellationToken cancellationToken)
    {
        var normalized = TryNormalize(slug);
        return normalized is null
            ? null
            : await _productRepository.GetPublicBySlugAsync(tenantId, normalized, cancellationToken);
    }

    /// <summary>
    /// A malformed slug from an anonymous URL is a miss, not a server-side invariant
    /// violation, so normalization failure becomes a clean not-found.
    /// </summary>
    private static string? TryNormalize(string slug)
    {
        try
        {
            return Category.NormalizeSlug(slug);
        }
        catch (DomainException)
        {
            return null;
        }
    }
}
