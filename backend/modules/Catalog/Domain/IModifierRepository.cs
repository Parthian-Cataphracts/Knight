namespace Catalog.Domain;

/// <summary>Persistence contract for <see cref="Modifier"/>.</summary>
public interface IModifierRepository
{
    Task<Modifier?> GetByIdAsync(Guid tenantId, Guid modifierGroupId, Guid modifierId, CancellationToken cancellationToken);

    /// <summary>Unpaged — the number of modifiers per group is bounded by design.</summary>
    Task<IReadOnlyCollection<Modifier>> ListByGroupAsync(Guid tenantId, Guid modifierGroupId, CancellationToken cancellationToken);

    Task AddAsync(Modifier modifier, CancellationToken cancellationToken);

    Task SaveChangesAsync(CancellationToken cancellationToken);
}
