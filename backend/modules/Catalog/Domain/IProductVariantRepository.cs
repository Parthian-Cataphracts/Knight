namespace Catalog.Domain;

/// <summary>
/// Persistence contract for <see cref="ProductVariant"/>. The "at most one default
/// variant per product" invariant has a partial unique index behind it, so the
/// flag swap must happen inside a single transaction — see <see cref="SetDefaultAsync"/>.
/// </summary>
public interface IProductVariantRepository
{
    Task<ProductVariant?> GetByIdAsync(Guid tenantId, Guid productId, Guid variantId, CancellationToken cancellationToken);

    /// <summary>Unpaged — the number of variants per product is bounded by design.</summary>
    Task<IReadOnlyCollection<ProductVariant>> ListByProductAsync(Guid tenantId, Guid productId, CancellationToken cancellationToken);

    Task AddAsync(ProductVariant variant, CancellationToken cancellationToken);

    Task SaveChangesAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Atomically clears the default flag on every other variant of the product and
    /// sets it on <paramref name="variantId"/>. Returns <c>false</c> when the variant
    /// does not exist for that tenant/product, so callers can respond with 404.
    /// </summary>
    Task<bool> SetDefaultAsync(Guid tenantId, Guid productId, Guid variantId, DateTimeOffset now, CancellationToken cancellationToken);
}
