namespace Catalog.Domain;

/// <summary>
/// A modifier group as it applies to one product, together with its modifiers,
/// flattened for storefront rendering. The entities carry no navigation
/// properties, so composition happens here rather than through EF includes.
/// </summary>
public sealed record ProductModifierGroupDetail
{
    public required ModifierGroup Group { get; init; }

    /// <summary>Ordering of the group within the product, from the assignment row.</summary>
    public required int DisplayOrder { get; init; }

    public required IReadOnlyCollection<Modifier> Modifiers { get; init; }
}

/// <summary>
/// Everything a storefront product detail page needs, loaded in one repository
/// call so the caller never fans out into per-collection round trips.
/// </summary>
public sealed record ProductDetail
{
    public required Product Product { get; init; }

    public required IReadOnlyCollection<ProductVariant> Variants { get; init; }

    public required IReadOnlyCollection<ProductModifierGroupDetail> ModifierGroups { get; init; }

    public required IReadOnlyCollection<ProductMedia> Media { get; init; }
}

/// <summary>
/// Persistence contract for <see cref="Product"/>. Products are never physically
/// deleted — removal is <see cref="Product.Archive"/> followed by a save — so no
/// delete method is exposed.
/// </summary>
public interface IProductRepository
{
    Task<Product?> GetByIdAsync(Guid tenantId, Guid productId, CancellationToken cancellationToken);

    /// <param name="normalizedSlug">Must already be normalized via <see cref="Product.NormalizeSlug"/>.</param>
    Task<Product?> GetBySlugAsync(Guid tenantId, string normalizedSlug, CancellationToken cancellationToken);

    Task<(IReadOnlyCollection<Product> Items, long TotalCount)> ListAsync(
        Guid tenantId,
        int page,
        int pageSize,
        Guid? categoryId,
        ProductStatus? status,
        bool? isVisible,
        bool? isAvailable,
        string? search,
        CancellationToken cancellationToken);

    /// <summary>Storefront listing: visible, non-archived products only.</summary>
    Task<(IReadOnlyCollection<Product> Items, long TotalCount)> ListPublicAsync(
        Guid tenantId,
        int page,
        int pageSize,
        Guid? categoryId,
        string? search,
        CancellationToken cancellationToken);

    /// <summary>
    /// Storefront product detail. Returns <c>null</c> when the product is hidden or
    /// archived so callers surface a clean not-found instead of leaking its existence.
    /// </summary>
    Task<ProductDetail?> GetPublicBySlugAsync(Guid tenantId, string normalizedSlug, CancellationToken cancellationToken);

    Task AddAsync(Product product, CancellationToken cancellationToken);

    Task SaveChangesAsync(CancellationToken cancellationToken);
}
