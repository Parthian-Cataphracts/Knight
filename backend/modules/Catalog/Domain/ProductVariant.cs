using Knight.Domain.Common;
using Knight.Domain.Exceptions;

namespace Catalog.Domain;

/// <summary>
/// A purchasable variation of a <see cref="Product"/>. <see cref="NormalizedSku"/>
/// is the uppercase form the uniqueness index is declared on, so SKU comparison
/// never depends on how the value was typed.
/// The "exactly one default variant per product" invariant is enforced
/// transactionally in the persistence layer; this entity only owns the flag.
/// </summary>
public sealed class ProductVariant : AuditableEntity, ITenantScoped
{
    private const int MaxNameLength = 150;
    private const int MaxSkuLength = 100;

    public Guid TenantId { get; private set; }

    public Guid ProductId { get; private set; }

    public string Name { get; private set; }

    public string? Sku { get; private set; }

    public string? NormalizedSku { get; private set; }

    public decimal Price { get; private set; }

    public decimal? CompareAtPrice { get; private set; }

    public bool IsDefault { get; private set; }

    public bool IsAvailable { get; private set; }

    public int DisplayOrder { get; private set; }

    private ProductVariant()
    {
        Name = string.Empty;
    }

    private ProductVariant(
        Guid id,
        DateTimeOffset createdAt,
        Guid tenantId,
        Guid productId,
        string name,
        string? sku,
        string? normalizedSku,
        decimal price,
        decimal? compareAtPrice,
        bool isDefault,
        bool isAvailable,
        int displayOrder)
        : base(id, createdAt)
    {
        TenantId = tenantId;
        ProductId = productId;
        Name = name;
        Sku = sku;
        NormalizedSku = normalizedSku;
        Price = price;
        CompareAtPrice = compareAtPrice;
        IsDefault = isDefault;
        IsAvailable = isAvailable;
        DisplayOrder = displayOrder;
    }

    public static ProductVariant Create(
        Guid id,
        DateTimeOffset now,
        Guid tenantId,
        Guid productId,
        string name,
        string? sku,
        decimal price,
        decimal? compareAtPrice,
        bool isDefault,
        bool isAvailable,
        int displayOrder)
    {
        if (tenantId == Guid.Empty)
        {
            throw DomainException.Validation("A product variant must belong to a tenant.");
        }

        if (productId == Guid.Empty)
        {
            throw DomainException.Validation("A product variant must belong to a product.");
        }

        var (displaySku, normalizedSku) = NormalizeSku(sku);

        return new ProductVariant(
            id,
            now,
            tenantId,
            productId,
            ValidateName(name),
            displaySku,
            normalizedSku,
            ValidatePrice(price, "Product variant price"),
            ValidateOptionalPrice(compareAtPrice),
            isDefault,
            isAvailable,
            displayOrder);
    }

    public void UpdateDetails(
        string name,
        string? sku,
        decimal price,
        decimal? compareAtPrice,
        bool isAvailable,
        int displayOrder,
        DateTimeOffset now)
    {
        var (displaySku, normalizedSku) = NormalizeSku(sku);

        Name = ValidateName(name);
        Sku = displaySku;
        NormalizedSku = normalizedSku;
        Price = ValidatePrice(price, "Product variant price");
        CompareAtPrice = ValidateOptionalPrice(compareAtPrice);
        IsAvailable = isAvailable;
        DisplayOrder = displayOrder;
        MarkUpdated(now);
    }

    public void MarkAsDefault(DateTimeOffset now)
    {
        IsDefault = true;
        MarkUpdated(now);
    }

    public void ClearDefault(DateTimeOffset now)
    {
        IsDefault = false;
        MarkUpdated(now);
    }

    public void SetAvailability(bool isAvailable, DateTimeOffset now)
    {
        IsAvailable = isAvailable;
        MarkUpdated(now);
    }

    public void ChangePrice(decimal price, DateTimeOffset now)
    {
        Price = ValidatePrice(price, "Product variant price");
        MarkUpdated(now);
    }

    private static string ValidateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw DomainException.Validation("Product variant name is required.");
        }

        var trimmed = name.Trim();
        if (trimmed.Length > MaxNameLength)
        {
            throw DomainException.Validation($"Product variant name cannot exceed {MaxNameLength} characters.");
        }

        return trimmed;
    }

    private static (string? Sku, string? NormalizedSku) NormalizeSku(string? sku)
    {
        if (string.IsNullOrWhiteSpace(sku))
        {
            return (null, null);
        }

        var trimmed = sku.Trim();
        if (trimmed.Length > MaxSkuLength)
        {
            throw DomainException.Validation($"Product variant SKU cannot exceed {MaxSkuLength} characters.");
        }

        return (trimmed, trimmed.ToUpperInvariant());
    }

    private static decimal ValidatePrice(decimal price, string label)
    {
        if (price < 0m)
        {
            throw DomainException.Validation($"{label} cannot be negative.");
        }

        return price;
    }

    private static decimal? ValidateOptionalPrice(decimal? price) =>
        price is null ? null : ValidatePrice(price.Value, "Product variant compare-at price");
}
