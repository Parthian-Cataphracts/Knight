using Catalog.Domain;
using Knight.Application.Abstractions.Time;
using Knight.Application.Exceptions;

namespace Catalog;

public sealed class ModifierGroupManagementService : IModifierGroupManagementService
{
    private const string GroupEntityType = nameof(ModifierGroup);
    private const string ModifierEntityType = nameof(Modifier);
    private const string AuditAction = "ModifierConfigurationChanged";

    private readonly IModifierGroupRepository _groupRepository;
    private readonly IModifierRepository _modifierRepository;
    private readonly IDateTimeProvider _dateTimeProvider;
    private readonly CatalogAuditRecorder _audit;

    public ModifierGroupManagementService(
        IModifierGroupRepository groupRepository,
        IModifierRepository modifierRepository,
        IDateTimeProvider dateTimeProvider,
        CatalogAuditRecorder audit)
    {
        _groupRepository = groupRepository;
        _modifierRepository = modifierRepository;
        _dateTimeProvider = dateTimeProvider;
        _audit = audit;
    }

    public async Task<ModifierGroup> CreateAsync(Guid tenantId, CreateModifierGroupInput input, CancellationToken cancellationToken)
    {
        var group = ModifierGroup.Create(
            Guid.NewGuid(),
            _dateTimeProvider.UtcNow,
            tenantId,
            input.Name,
            input.IsRequired,
            input.MinSelections,
            input.MaxSelections,
            input.DisplayOrder);

        await _groupRepository.AddAsync(group, cancellationToken);

        await _audit.RecordAsync(AuditAction, tenantId, GroupEntityType, group.Id, cancellationToken, new Dictionary<string, string>
        {
            ["operation"] = "GroupCreated",
            ["name"] = group.Name
        });

        return group;
    }

    public Task<ModifierGroup?> GetAsync(Guid tenantId, Guid modifierGroupId, CancellationToken cancellationToken) =>
        _groupRepository.GetByIdAsync(tenantId, modifierGroupId, cancellationToken);

    public async Task<ModifierGroupListResult> ListAsync(Guid tenantId, int page, int pageSize, string? search, CancellationToken cancellationToken)
    {
        var (boundedPage, boundedPageSize) = CatalogPaging.Bound(page, pageSize);

        var (items, total) = await _groupRepository.ListAsync(tenantId, boundedPage, boundedPageSize, search, cancellationToken);
        return new ModifierGroupListResult(items, total, boundedPage, boundedPageSize);
    }

    public async Task<ModifierGroup> UpdateAsync(Guid tenantId, Guid modifierGroupId, UpdateModifierGroupInput input, CancellationToken cancellationToken)
    {
        var group = await RequireGroupAsync(tenantId, modifierGroupId, cancellationToken);

        group.UpdateDetails(
            input.Name,
            input.IsRequired,
            input.MinSelections,
            input.MaxSelections,
            input.DisplayOrder,
            _dateTimeProvider.UtcNow);

        await _groupRepository.SaveChangesAsync(cancellationToken);

        await _audit.RecordAsync(AuditAction, tenantId, GroupEntityType, group.Id, cancellationToken, new Dictionary<string, string>
        {
            ["operation"] = "GroupUpdated",
            ["name"] = group.Name
        });

        return group;
    }

    public async Task DeleteAsync(Guid tenantId, Guid modifierGroupId, CancellationToken cancellationToken)
    {
        var group = await RequireGroupAsync(tenantId, modifierGroupId, cancellationToken);

        // Consistent with the category rule: refuse the delete rather than leave
        // products pointing at a group that no longer exists.
        if (await _groupRepository.HasAssignmentsAsync(tenantId, modifierGroupId, cancellationToken))
        {
            throw new ConflictException($"Modifier group '{group.Name}' is still assigned to one or more products and cannot be deleted.");
        }

        await _groupRepository.DeleteAsync(group, cancellationToken);

        await _audit.RecordAsync(AuditAction, tenantId, GroupEntityType, modifierGroupId, cancellationToken, new Dictionary<string, string>
        {
            ["operation"] = "GroupDeleted",
            ["name"] = group.Name
        });
    }

    public async Task<Modifier> CreateModifierAsync(Guid tenantId, Guid modifierGroupId, CreateModifierInput input, CancellationToken cancellationToken)
    {
        await RequireGroupAsync(tenantId, modifierGroupId, cancellationToken);

        var modifier = Modifier.Create(
            Guid.NewGuid(),
            _dateTimeProvider.UtcNow,
            tenantId,
            modifierGroupId,
            input.Name,
            input.PriceDelta,
            input.IsAvailable,
            input.DisplayOrder);

        await _modifierRepository.AddAsync(modifier, cancellationToken);

        await _audit.RecordAsync(AuditAction, tenantId, ModifierEntityType, modifier.Id, cancellationToken, new Dictionary<string, string>
        {
            ["operation"] = "ModifierCreated",
            ["modifierGroupId"] = modifierGroupId.ToString(),
            ["name"] = modifier.Name
        });

        return modifier;
    }

    public Task<Modifier?> GetModifierAsync(Guid tenantId, Guid modifierGroupId, Guid modifierId, CancellationToken cancellationToken) =>
        _modifierRepository.GetByIdAsync(tenantId, modifierGroupId, modifierId, cancellationToken);

    public Task<IReadOnlyCollection<Modifier>> ListModifiersAsync(Guid tenantId, Guid modifierGroupId, CancellationToken cancellationToken) =>
        _modifierRepository.ListByGroupAsync(tenantId, modifierGroupId, cancellationToken);

    public async Task<Modifier> UpdateModifierAsync(Guid tenantId, Guid modifierGroupId, Guid modifierId, UpdateModifierInput input, CancellationToken cancellationToken)
    {
        var modifier = await RequireModifierAsync(tenantId, modifierGroupId, modifierId, cancellationToken);

        modifier.UpdateDetails(input.Name, input.PriceDelta, input.IsAvailable, input.DisplayOrder, _dateTimeProvider.UtcNow);
        await _modifierRepository.SaveChangesAsync(cancellationToken);

        await _audit.RecordAsync(AuditAction, tenantId, ModifierEntityType, modifier.Id, cancellationToken, new Dictionary<string, string>
        {
            ["operation"] = "ModifierUpdated",
            ["modifierGroupId"] = modifierGroupId.ToString(),
            ["name"] = modifier.Name
        });

        return modifier;
    }

    public async Task<Modifier> SetModifierAvailabilityAsync(Guid tenantId, Guid modifierGroupId, Guid modifierId, bool isAvailable, CancellationToken cancellationToken)
    {
        var modifier = await RequireModifierAsync(tenantId, modifierGroupId, modifierId, cancellationToken);

        modifier.SetAvailability(isAvailable, _dateTimeProvider.UtcNow);
        await _modifierRepository.SaveChangesAsync(cancellationToken);

        await _audit.RecordAsync(AuditAction, tenantId, ModifierEntityType, modifier.Id, cancellationToken, new Dictionary<string, string>
        {
            ["operation"] = "ModifierAvailabilityChanged",
            ["modifierGroupId"] = modifierGroupId.ToString(),
            ["isAvailable"] = isAvailable.ToString()
        });

        return modifier;
    }

    private async Task<ModifierGroup> RequireGroupAsync(Guid tenantId, Guid modifierGroupId, CancellationToken cancellationToken) =>
        await _groupRepository.GetByIdAsync(tenantId, modifierGroupId, cancellationToken)
            ?? throw new NotFoundException(nameof(ModifierGroup), modifierGroupId);

    private async Task<Modifier> RequireModifierAsync(Guid tenantId, Guid modifierGroupId, Guid modifierId, CancellationToken cancellationToken) =>
        await _modifierRepository.GetByIdAsync(tenantId, modifierGroupId, modifierId, cancellationToken)
            ?? throw new NotFoundException(nameof(Modifier), modifierId);
}
