using Catalog.Domain;

namespace Catalog;

public sealed record ProductModifierGroupAssignment(Guid ModifierGroupId, int DisplayOrder);

/// <summary>
/// Assignment of modifier groups to a product. Assignments are replaced as a set
/// rather than added and removed one at a time, so the product's modifier layout
/// is never observable in a half-applied state.
/// </summary>
public interface IProductModifierAssignmentService
{
    Task<IReadOnlyCollection<ProductModifierGroup>> ListAsync(Guid tenantId, Guid productId, CancellationToken cancellationToken);

    Task ReplaceAsync(Guid tenantId, Guid productId, IReadOnlyCollection<ProductModifierGroupAssignment> assignments, CancellationToken cancellationToken);
}
