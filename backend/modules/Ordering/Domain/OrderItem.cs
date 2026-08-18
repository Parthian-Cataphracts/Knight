using Knight.Domain.Common;
using Knight.Domain.Exceptions;

namespace Ordering.Domain;

/// <summary>
/// An immutable historical snapshot of a line item in an <see cref="Order"/>.
/// Stores frozen product/variant names, base price, modifier totals, and line total.
/// </summary>
public sealed class OrderItem : Entity, ITenantScoped
{
    private const int MaxNameLength = 150;
    private const int MaxQuantity = 999;

    public Guid TenantId { get; private set; }

    public Guid OrderId { get; private set; }

    public Guid SourceProductId { get; private set; }

    public string ProductName { get; private set; }

    public Guid? SourceVariantId { get; private set; }

    public string? VariantName { get; private set; }

    public decimal UnitBasePrice { get; private set; }

    public int Quantity { get; private set; }

    public decimal UnitModifierTotal { get; private set; }

    public decimal UnitPrice { get; private set; }

    public decimal LineTotal { get; private set; }

    public int DisplayOrder { get; private set; }

    private readonly List<OrderItemModifier> _modifiers = [];

    public IReadOnlyCollection<OrderItemModifier> Modifiers => _modifiers.AsReadOnly();

    private OrderItem()
    {
        ProductName = string.Empty;
    }

    private OrderItem(
        Guid id,
        Guid tenantId,
        Guid orderId,
        Guid sourceProductId,
        string productName,
        Guid? sourceVariantId,
        string? variantName,
        decimal unitBasePrice,
        int quantity,
        decimal unitModifierTotal,
        decimal unitPrice,
        decimal lineTotal,
        int displayOrder,
        IEnumerable<OrderItemModifier> modifiers)
        : base(id)
    {
        TenantId = tenantId;
        OrderId = orderId;
        SourceProductId = sourceProductId;
        ProductName = productName;
        SourceVariantId = sourceVariantId;
        VariantName = variantName;
        UnitBasePrice = unitBasePrice;
        Quantity = quantity;
        UnitModifierTotal = unitModifierTotal;
        UnitPrice = unitPrice;
        LineTotal = lineTotal;
        DisplayOrder = displayOrder;
        _modifiers.AddRange(modifiers);
    }

    public static OrderItem Create(
        Guid id,
        Guid tenantId,
        Guid orderId,
        Guid sourceProductId,
        string productName,
        Guid? sourceVariantId,
        string? variantName,
        decimal unitBasePrice,
        int quantity,
        int displayOrder,
        IEnumerable<OrderItemModifier>? modifiers = null)
    {
        if (tenantId == Guid.Empty)
        {
            throw DomainException.Validation("Tenant ID is required.");
        }

        if (orderId == Guid.Empty)
        {
            throw DomainException.Validation("Order ID is required.");
        }

        if (sourceProductId == Guid.Empty)
        {
            throw DomainException.Validation("Source product ID is required.");
        }

        if (string.IsNullOrWhiteSpace(productName))
        {
            throw DomainException.Validation("Product name is required.");
        }

        if (productName.Length > MaxNameLength)
        {
            throw DomainException.Validation($"Product name cannot exceed {MaxNameLength} characters.");
        }

        if (variantName is not null && variantName.Length > MaxNameLength)
        {
            throw DomainException.Validation($"Variant name cannot exceed {MaxNameLength} characters.");
        }

        if (unitBasePrice < 0)
        {
            throw DomainException.Validation("Unit base price cannot be negative.");
        }

        if (quantity <= 0)
        {
            throw DomainException.Validation("Quantity must be greater than zero.");
        }

        if (quantity > MaxQuantity)
        {
            throw DomainException.Validation($"Quantity cannot exceed {MaxQuantity}.");
        }

        var modifierList = modifiers?.ToList() ?? [];
        var unitModifierTotal = modifierList.Sum(m => m.UnitPriceDelta);
        var unitPrice = unitBasePrice + unitModifierTotal;
        var lineTotal = unitPrice * quantity;

        return new OrderItem(
            id,
            tenantId,
            orderId,
            sourceProductId,
            productName.Trim(),
            sourceVariantId,
            variantName?.Trim(),
            unitBasePrice,
            quantity,
            unitModifierTotal,
            unitPrice,
            lineTotal,
            displayOrder,
            modifierList);
    }
}
