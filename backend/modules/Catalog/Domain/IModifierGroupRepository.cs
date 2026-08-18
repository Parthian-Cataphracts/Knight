namespace Catalog.Domain;

/// <summary>Persistence contract for <see cref="ModifierGroup"/>.</summary>
public interface IModifierGroupRepository
{
    Task<ModifierGroup?> GetByIdAsync(Guid tenantId, Guid modifierGroupId, CancellationToken cancellationToken);

    Task<(IReadOnlyCollection<ModifierGroup> Items, long TotalCount)> ListAsync(Guid tenantId, int page, int pageSize, string? search, CancellationToken cancellationToken);

    Task AddAsync(ModifierGroup modifierGroup, CancellationToken cancellationToken);

    /// <summary>Deletes the group. Callers must have already verified it has no product assignments.</summary>
    Task DeleteAsync(ModifierGroup modifierGroup, CancellationToken cancellationToken);

    Task SaveChangesAsync(CancellationToken cancellationToken);

    /// <summary>True when any product still references this group.</summary>
    Task<bool> HasAssignmentsAsync(Guid tenantId, Guid modifierGroupId, CancellationToken cancellationToken);
}
