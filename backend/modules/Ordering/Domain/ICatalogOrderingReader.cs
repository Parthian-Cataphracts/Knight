namespace Ordering.Domain;

public sealed record OrderableProductSnapshot(
    Guid Id,
    Guid TenantId,
    string Name,
    decimal BasePrice,
    bool IsVisible,
    bool IsAvailable,
    string Status,
    IReadOnlyCollection<OrderableVariantSnapshot> Variants,
    IReadOnlyCollection<OrderableModifierGroupSnapshot> ModifierGroups);

public sealed record OrderableVariantSnapshot(
    Guid Id,
    Guid ProductId,
    Guid TenantId,
    string Name,
    decimal Price,
    bool IsAvailable,
    bool IsDefault);

/// <param name="AssignmentDisplayOrder">
/// Position of this group within the owning product, from the Catalog
/// product-to-group assignment. The primary key of the canonical modifier
/// ordering — see <see cref="OrderableModifierSnapshot.DisplayOrder"/>.
/// </param>
/// <param name="GroupDisplayOrder">
/// The group's own Catalog position, used only to break ties between two
/// assignments that share an <paramref name="AssignmentDisplayOrder"/>.
/// </param>
public sealed record OrderableModifierGroupSnapshot(
    Guid Id,
    Guid TenantId,
    string Name,
    bool IsRequired,
    int MinSelections,
    int MaxSelections,
    int AssignmentDisplayOrder,
    int GroupDisplayOrder,
    IReadOnlyCollection<OrderableModifierSnapshot> Modifiers);

/// <param name="DisplayOrder">
/// The modifier's Catalog position within its group. Together with the owning
/// group's ordering this makes modifier ordering server-authoritative, so the
/// order in which a client happens to list modifier ids cannot change what is
/// persisted — see <c>OrderPricingCalculator</c>.
/// </param>
public sealed record OrderableModifierSnapshot(
    Guid Id,
    Guid ModifierGroupId,
    Guid TenantId,
    string Name,
    decimal PriceDelta,
    bool IsAvailable,
    int DisplayOrder);

/// <summary>
/// Cross-module read port enabling Ordering to resolve and validate current Catalog state
/// during order placement without referencing Catalog EF entities or domain types directly.
/// </summary>
public interface ICatalogOrderingReader
{
    Task<IReadOnlyDictionary<Guid, OrderableProductSnapshot>> GetOrderableProductsAsync(
        Guid tenantId,
        IReadOnlyCollection<Guid> productIds,
        CancellationToken cancellationToken);
}
