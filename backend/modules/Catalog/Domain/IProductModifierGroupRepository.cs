namespace Catalog.Domain;

/// <summary>Persistence contract for <see cref="ProductModifierGroup"/> assignments.</summary>
public interface IProductModifierGroupRepository
{
    Task<IReadOnlyCollection<ProductModifierGroup>> ListByProductAsync(Guid tenantId, Guid productId, CancellationToken cancellationToken);

    /// <summary>
    /// Atomically replaces the product's modifier-group assignments with exactly
    /// <paramref name="assignments"/>.
    /// </summary>
    Task ReplaceForProductAsync(
        Guid tenantId,
        Guid productId,
        IReadOnlyCollection<(Guid ModifierGroupId, int DisplayOrder)> assignments,
        DateTimeOffset now,
        CancellationToken cancellationToken);
}
