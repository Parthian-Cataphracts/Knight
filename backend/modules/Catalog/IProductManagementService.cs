using Catalog.Domain;

namespace Catalog;

public sealed record CreateProductInput(
    Guid CategoryId,
    string Name,
    string? Slug,
    string? Description,
    ProductStatus Status,
    decimal BasePrice,
    bool IsVisible,
    bool IsAvailable,
    int DisplayOrder);

public sealed record UpdateProductInput(
    string Name,
    string? Slug,
    string? Description,
    decimal BasePrice,
    bool IsVisible,
    bool IsAvailable,
    int DisplayOrder);

/// <summary>Admin listing filters. Every member is optional; nulls mean "no filter".</summary>
public sealed record ProductListFilter(
    Guid? CategoryId = null,
    ProductStatus? Status = null,
    bool? IsVisible = null,
    bool? IsAvailable = null,
    string? Search = null);

public sealed record ProductListResult(IReadOnlyCollection<Product> Items, long TotalCount, int Page, int PageSize);

/// <summary>
/// Tenant catalog product administration. Products are never physically removed —
/// <see cref="ArchiveAsync"/> is the delete operation.
/// </summary>
public interface IProductManagementService
{
    Task<Product> CreateAsync(Guid tenantId, CreateProductInput input, CancellationToken cancellationToken);

    Task<Product?> GetAsync(Guid tenantId, Guid productId, CancellationToken cancellationToken);

    Task<ProductListResult> ListAsync(Guid tenantId, int page, int pageSize, ProductListFilter filter, CancellationToken cancellationToken);

    Task<Product> UpdateAsync(Guid tenantId, Guid productId, UpdateProductInput input, CancellationToken cancellationToken);

    Task<Product> ChangeCategoryAsync(Guid tenantId, Guid productId, Guid categoryId, CancellationToken cancellationToken);

    Task<Product> ActivateAsync(Guid tenantId, Guid productId, CancellationToken cancellationToken);

    /// <summary>The catalog's "delete" semantics: marks the product archived, never removes the row.</summary>
    Task<Product> ArchiveAsync(Guid tenantId, Guid productId, CancellationToken cancellationToken);

    Task<Product> SetVisibilityAsync(Guid tenantId, Guid productId, bool isVisible, CancellationToken cancellationToken);

    Task<Product> SetAvailabilityAsync(Guid tenantId, Guid productId, bool isAvailable, CancellationToken cancellationToken);
}
