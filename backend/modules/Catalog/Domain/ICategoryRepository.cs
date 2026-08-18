namespace Catalog.Domain;

/// <summary>
/// Persistence contract for <see cref="Category"/>. Deliberately specific rather
/// than a generic repository abstraction, mirroring the Identity module.
/// </summary>
public interface ICategoryRepository
{
    Task<Category?> GetByIdAsync(Guid tenantId, Guid categoryId, CancellationToken cancellationToken);

    /// <param name="normalizedSlug">Must already be normalized via <see cref="Category.NormalizeSlug"/>.</param>
    Task<Category?> GetBySlugAsync(Guid tenantId, string normalizedSlug, CancellationToken cancellationToken);

    Task<(IReadOnlyCollection<Category> Items, long TotalCount)> ListAsync(Guid tenantId, int page, int pageSize, string? search, bool? isVisible, CancellationToken cancellationToken);

    Task AddAsync(Category category, CancellationToken cancellationToken);

    /// <summary>Physically deletes the category. Callers must have already verified it has no products.</summary>
    Task DeleteAsync(Category category, CancellationToken cancellationToken);

    Task SaveChangesAsync(CancellationToken cancellationToken);

    Task<bool> HasProductsAsync(Guid tenantId, Guid categoryId, CancellationToken cancellationToken);
}
