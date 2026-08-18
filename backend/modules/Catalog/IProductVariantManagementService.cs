using Catalog.Domain;

namespace Catalog;

public sealed record CreateProductVariantInput(
    string Name,
    string? Sku,
    decimal Price,
    decimal? CompareAtPrice,
    bool IsAvailable,
    int DisplayOrder);

public sealed record UpdateProductVariantInput(
    string Name,
    string? Sku,
    decimal Price,
    decimal? CompareAtPrice,
    bool IsAvailable,
    int DisplayOrder);

/// <summary>
/// Product variant administration. Variants are never physically removed —
/// <see cref="DeactivateAsync"/> marks them unavailable so future order history
/// can still resolve them.
/// </summary>
public interface IProductVariantManagementService
{
    /// <summary>
    /// Creates a variant. The first variant created for a product automatically
    /// becomes its default; later ones do not unless <see cref="SetDefaultAsync"/> is called.
    /// </summary>
    Task<ProductVariant> CreateAsync(Guid tenantId, Guid productId, CreateProductVariantInput input, CancellationToken cancellationToken);

    Task<ProductVariant?> GetAsync(Guid tenantId, Guid productId, Guid variantId, CancellationToken cancellationToken);

    Task<IReadOnlyCollection<ProductVariant>> ListAsync(Guid tenantId, Guid productId, CancellationToken cancellationToken);

    Task<ProductVariant> UpdateAsync(Guid tenantId, Guid productId, Guid variantId, UpdateProductVariantInput input, CancellationToken cancellationToken);

    Task SetDefaultAsync(Guid tenantId, Guid productId, Guid variantId, CancellationToken cancellationToken);

    Task<ProductVariant> SetAvailabilityAsync(Guid tenantId, Guid productId, Guid variantId, bool isAvailable, CancellationToken cancellationToken);

    /// <summary>The variant "delete" operation: marks the variant unavailable rather than removing it.</summary>
    Task DeactivateAsync(Guid tenantId, Guid productId, Guid variantId, CancellationToken cancellationToken);
}
