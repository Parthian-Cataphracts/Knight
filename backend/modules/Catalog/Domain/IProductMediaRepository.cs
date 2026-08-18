namespace Catalog.Domain;

/// <summary>
/// Persistence contract for <see cref="ProductMedia"/>. The "at most one primary
/// media per product" invariant has a partial unique index behind it, so the flag
/// swap must happen inside a single transaction — see <see cref="SetPrimaryAsync"/>.
/// </summary>
public interface IProductMediaRepository
{
    Task<ProductMedia?> GetByIdAsync(Guid tenantId, Guid productId, Guid mediaId, CancellationToken cancellationToken);

    /// <summary>Unpaged, ordered by display order.</summary>
    Task<IReadOnlyCollection<ProductMedia>> ListByProductAsync(Guid tenantId, Guid productId, CancellationToken cancellationToken);

    Task AddAsync(ProductMedia media, CancellationToken cancellationToken);

    Task DeleteAsync(ProductMedia media, CancellationToken cancellationToken);

    Task SaveChangesAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Atomically clears the primary flag on every other media row of the product and
    /// sets it on <paramref name="mediaId"/>. Returns <c>false</c> when the row does
    /// not exist for that tenant/product, so callers can respond with 404.
    /// </summary>
    Task<bool> SetPrimaryAsync(Guid tenantId, Guid productId, Guid mediaId, CancellationToken cancellationToken);
}
