using Knight.Domain.Common;
using Knight.Domain.Exceptions;

namespace Catalog.Domain;

/// <summary>
/// Assigns one <see cref="ModifierGroup"/> to one <see cref="Product"/>.
/// <see cref="TenantId"/> is denormalized here so the database can enforce that
/// both sides belong to the same tenant with real composite foreign keys — see
/// docs/architecture/multi-tenancy.md.
/// </summary>
public sealed class ProductModifierGroup : Entity, ITenantScoped
{
    public Guid TenantId { get; private set; }

    public Guid ProductId { get; private set; }

    public Guid ModifierGroupId { get; private set; }

    public int DisplayOrder { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    private ProductModifierGroup()
    {
    }

    private ProductModifierGroup(
        Guid id,
        DateTimeOffset createdAt,
        Guid tenantId,
        Guid productId,
        Guid modifierGroupId,
        int displayOrder)
        : base(id)
    {
        TenantId = tenantId;
        ProductId = productId;
        ModifierGroupId = modifierGroupId;
        DisplayOrder = displayOrder;
        CreatedAt = createdAt;
    }

    public static ProductModifierGroup Create(
        Guid id,
        DateTimeOffset createdAt,
        Guid tenantId,
        Guid productId,
        Guid modifierGroupId,
        int displayOrder)
    {
        if (tenantId == Guid.Empty || productId == Guid.Empty || modifierGroupId == Guid.Empty)
        {
            throw DomainException.Validation(
                "A modifier group assignment must reference a tenant, a product, and a modifier group.");
        }

        return new ProductModifierGroup(id, createdAt, tenantId, productId, modifierGroupId, displayOrder);
    }
}
