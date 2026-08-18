using Catalog.Domain;

namespace Catalog;

public sealed record CreateCategoryInput(string Name, string? Slug, string? Description, bool IsVisible, int DisplayOrder);

public sealed record UpdateCategoryInput(string Name, string? Slug, string? Description, bool IsVisible, int DisplayOrder);

public sealed record CategoryListResult(IReadOnlyCollection<Category> Items, long TotalCount, int Page, int PageSize);

/// <summary>
/// Tenant catalog category administration. Every method takes the tenant as an
/// explicit parameter — the caller (endpoint) resolves it from the request
/// context, mirroring the Identity module's management services.
/// </summary>
public interface ICategoryManagementService
{
    Task<Category> CreateAsync(Guid tenantId, CreateCategoryInput input, CancellationToken cancellationToken);

    Task<Category?> GetAsync(Guid tenantId, Guid categoryId, CancellationToken cancellationToken);

    Task<CategoryListResult> ListAsync(Guid tenantId, int page, int pageSize, string? search, bool? isVisible, CancellationToken cancellationToken);

    Task<Category> UpdateAsync(Guid tenantId, Guid categoryId, UpdateCategoryInput input, CancellationToken cancellationToken);

    Task<Category> SetVisibilityAsync(Guid tenantId, Guid categoryId, bool isVisible, CancellationToken cancellationToken);

    /// <summary>
    /// Physically deletes the category. Throws
    /// <see cref="Knight.Application.Exceptions.ConflictException"/> when products
    /// still reference it.
    /// </summary>
    Task DeleteAsync(Guid tenantId, Guid categoryId, CancellationToken cancellationToken);
}
