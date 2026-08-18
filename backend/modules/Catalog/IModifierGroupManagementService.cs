using Catalog.Domain;

namespace Catalog;

public sealed record CreateModifierGroupInput(string Name, bool IsRequired, int MinSelections, int MaxSelections, int DisplayOrder);

public sealed record UpdateModifierGroupInput(string Name, bool IsRequired, int MinSelections, int MaxSelections, int DisplayOrder);

public sealed record CreateModifierInput(string Name, decimal PriceDelta, bool IsAvailable, int DisplayOrder);

public sealed record UpdateModifierInput(string Name, decimal PriceDelta, bool IsAvailable, int DisplayOrder);

public sealed record ModifierGroupListResult(IReadOnlyCollection<ModifierGroup> Items, long TotalCount, int Page, int PageSize);

/// <summary>
/// Modifier group administration, including the modifiers each group owns —
/// modifiers have no lifecycle independent of their group, so they are managed
/// through the same service rather than a second near-empty one.
/// </summary>
public interface IModifierGroupManagementService
{
    Task<ModifierGroup> CreateAsync(Guid tenantId, CreateModifierGroupInput input, CancellationToken cancellationToken);

    Task<ModifierGroup?> GetAsync(Guid tenantId, Guid modifierGroupId, CancellationToken cancellationToken);

    Task<ModifierGroupListResult> ListAsync(Guid tenantId, int page, int pageSize, string? search, CancellationToken cancellationToken);

    Task<ModifierGroup> UpdateAsync(Guid tenantId, Guid modifierGroupId, UpdateModifierGroupInput input, CancellationToken cancellationToken);

    /// <summary>
    /// Physically deletes the group. Throws
    /// <see cref="Knight.Application.Exceptions.ConflictException"/> when products
    /// are still assigned to it.
    /// </summary>
    Task DeleteAsync(Guid tenantId, Guid modifierGroupId, CancellationToken cancellationToken);

    Task<Modifier> CreateModifierAsync(Guid tenantId, Guid modifierGroupId, CreateModifierInput input, CancellationToken cancellationToken);

    Task<Modifier?> GetModifierAsync(Guid tenantId, Guid modifierGroupId, Guid modifierId, CancellationToken cancellationToken);

    Task<IReadOnlyCollection<Modifier>> ListModifiersAsync(Guid tenantId, Guid modifierGroupId, CancellationToken cancellationToken);

    Task<Modifier> UpdateModifierAsync(Guid tenantId, Guid modifierGroupId, Guid modifierId, UpdateModifierInput input, CancellationToken cancellationToken);

    Task<Modifier> SetModifierAvailabilityAsync(Guid tenantId, Guid modifierGroupId, Guid modifierId, bool isAvailable, CancellationToken cancellationToken);
}
