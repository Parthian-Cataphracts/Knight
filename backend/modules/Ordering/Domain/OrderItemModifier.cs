using Knight.Domain.Common;
using Knight.Domain.Exceptions;

namespace Ordering.Domain;

/// <summary>
/// An immutable historical snapshot of a modifier selected for an <see cref="OrderItem"/>.
/// Subsequent edits or removals in the Catalog module do not affect historical orders.
/// </summary>
public sealed class OrderItemModifier : Entity, ITenantScoped
{
    private const int MaxNameLength = 150;

    public Guid TenantId { get; private set; }

    public Guid OrderItemId { get; private set; }

    public Guid SourceModifierGroupId { get; private set; }

    public string ModifierGroupName { get; private set; }

    public Guid SourceModifierId { get; private set; }

    public string ModifierName { get; private set; }

    public decimal UnitPriceDelta { get; private set; }

    public int DisplayOrder { get; private set; }

    private OrderItemModifier()
    {
        ModifierGroupName = string.Empty;
        ModifierName = string.Empty;
    }

    private OrderItemModifier(
        Guid id,
        Guid tenantId,
        Guid orderItemId,
        Guid sourceModifierGroupId,
        string modifierGroupName,
        Guid sourceModifierId,
        string modifierName,
        decimal unitPriceDelta,
        int displayOrder)
        : base(id)
    {
        TenantId = tenantId;
        OrderItemId = orderItemId;
        SourceModifierGroupId = sourceModifierGroupId;
        ModifierGroupName = modifierGroupName;
        SourceModifierId = sourceModifierId;
        ModifierName = modifierName;
        UnitPriceDelta = unitPriceDelta;
        DisplayOrder = displayOrder;
    }

    public static OrderItemModifier Create(
        Guid id,
        Guid tenantId,
        Guid orderItemId,
        Guid sourceModifierGroupId,
        string modifierGroupName,
        Guid sourceModifierId,
        string modifierName,
        decimal unitPriceDelta,
        int displayOrder)
    {
        if (tenantId == Guid.Empty)
        {
            throw DomainException.Validation("Tenant ID is required.");
        }

        if (orderItemId == Guid.Empty)
        {
            throw DomainException.Validation("Order item ID is required.");
        }

        if (sourceModifierGroupId == Guid.Empty)
        {
            throw DomainException.Validation("Source modifier group ID is required.");
        }

        if (string.IsNullOrWhiteSpace(modifierGroupName))
        {
            throw DomainException.Validation("Modifier group name is required.");
        }

        if (modifierGroupName.Length > MaxNameLength)
        {
            throw DomainException.Validation($"Modifier group name cannot exceed {MaxNameLength} characters.");
        }

        if (sourceModifierId == Guid.Empty)
        {
            throw DomainException.Validation("Source modifier ID is required.");
        }

        if (string.IsNullOrWhiteSpace(modifierName))
        {
            throw DomainException.Validation("Modifier name is required.");
        }

        if (modifierName.Length > MaxNameLength)
        {
            throw DomainException.Validation($"Modifier name cannot exceed {MaxNameLength} characters.");
        }

        if (unitPriceDelta < 0)
        {
            throw DomainException.Validation("Modifier price delta cannot be negative.");
        }

        return new OrderItemModifier(
            id,
            tenantId,
            orderItemId,
            sourceModifierGroupId,
            modifierGroupName.Trim(),
            sourceModifierId,
            modifierName.Trim(),
            unitPriceDelta,
            displayOrder);
    }
}
