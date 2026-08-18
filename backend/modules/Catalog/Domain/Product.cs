using Knight.Domain.Common;
using Knight.Domain.Exceptions;

namespace Catalog.Domain;

/// <summary>
/// A tenant-scoped catalog item belonging to exactly one <see cref="Category"/>.
/// Removal is modelled as <see cref="Archive"/> rather than a physical delete so
/// historical references to the product stay resolvable.
/// </summary>
public sealed class Product : AuditableEntity, ITenantScoped
{
    private const int MaxNameLength = 150;
    private const int MaxDescriptionLength = 2000;

    public Guid TenantId { get; private set; }

    public Guid CategoryId { get; private set; }

    public string Name { get; private set; }

    public string Slug { get; private set; }

    public string? Description { get; private set; }

    public ProductStatus Status { get; private set; }

    public decimal BasePrice { get; private set; }

    public bool IsVisible { get; private set; }

    public bool IsAvailable { get; private set; }

    public int DisplayOrder { get; private set; }

    private Product()
    {
        Name = string.Empty;
        Slug = string.Empty;
    }

    private Product(
        Guid id,
        DateTimeOffset createdAt,
        Guid tenantId,
        Guid categoryId,
        string name,
        string slug,
        string? description,
        ProductStatus status,
        decimal basePrice,
        bool isVisible,
        bool isAvailable,
        int displayOrder)
        : base(id, createdAt)
    {
        TenantId = tenantId;
        CategoryId = categoryId;
        Name = name;
        Slug = slug;
        Description = description;
        Status = status;
        BasePrice = basePrice;
        IsVisible = isVisible;
        IsAvailable = isAvailable;
        DisplayOrder = displayOrder;
    }

    public static Product Create(
        Guid id,
        DateTimeOffset now,
        Guid tenantId,
        Guid categoryId,
        string name,
        string? slug,
        string? description,
        ProductStatus status,
        decimal basePrice,
        bool isVisible,
        bool isAvailable,
        int displayOrder)
    {
        if (tenantId == Guid.Empty)
        {
            throw DomainException.Validation("A product must belong to a tenant.");
        }

        if (categoryId == Guid.Empty)
        {
            throw DomainException.Validation("A product must belong to a category.");
        }

        var validatedName = ValidateName(name);

        return new Product(
            id,
            now,
            tenantId,
            categoryId,
            validatedName,
            NormalizeSlug(string.IsNullOrWhiteSpace(slug) ? validatedName : slug),
            ValidateDescription(description),
            status,
            ValidatePrice(basePrice),
            isVisible,
            isAvailable,
            displayOrder);
    }

    public static string NormalizeSlug(string value) => SlugNormalizer.Normalize(value);

    public void UpdateDetails(
        string name,
        string? slug,
        string? description,
        decimal basePrice,
        bool isVisible,
        bool isAvailable,
        int displayOrder,
        DateTimeOffset now)
    {
        var validatedName = ValidateName(name);

        Name = validatedName;
        Slug = NormalizeSlug(string.IsNullOrWhiteSpace(slug) ? validatedName : slug);
        Description = ValidateDescription(description);
        BasePrice = ValidatePrice(basePrice);
        IsVisible = isVisible;
        IsAvailable = isAvailable;
        DisplayOrder = displayOrder;
        MarkUpdated(now);
    }

    public void ChangeCategory(Guid categoryId, DateTimeOffset now)
    {
        if (categoryId == Guid.Empty)
        {
            throw DomainException.Validation("A product must belong to a category.");
        }

        CategoryId = categoryId;
        MarkUpdated(now);
    }

    public void ChangeSlug(string slug, DateTimeOffset now)
    {
        Slug = NormalizeSlug(slug);
        MarkUpdated(now);
    }

    public void Rename(string name, DateTimeOffset now)
    {
        Name = ValidateName(name);
        MarkUpdated(now);
    }

    public void Activate(DateTimeOffset now)
    {
        Status = ProductStatus.Active;
        MarkUpdated(now);
    }

    public void Archive(DateTimeOffset now)
    {
        Status = ProductStatus.Archived;
        MarkUpdated(now);
    }

    public void SetVisibility(bool isVisible, DateTimeOffset now)
    {
        IsVisible = isVisible;
        MarkUpdated(now);
    }

    public void SetAvailability(bool isAvailable, DateTimeOffset now)
    {
        IsAvailable = isAvailable;
        MarkUpdated(now);
    }

    public void ChangeBasePrice(decimal price, DateTimeOffset now)
    {
        BasePrice = ValidatePrice(price);
        MarkUpdated(now);
    }

    private static string ValidateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw DomainException.Validation("Product name is required.");
        }

        var trimmed = name.Trim();
        if (trimmed.Length > MaxNameLength)
        {
            throw DomainException.Validation($"Product name cannot exceed {MaxNameLength} characters.");
        }

        return trimmed;
    }

    private static string? ValidateDescription(string? description)
    {
        if (string.IsNullOrWhiteSpace(description))
        {
            return null;
        }

        var trimmed = description.Trim();
        if (trimmed.Length > MaxDescriptionLength)
        {
            throw DomainException.Validation($"Product description cannot exceed {MaxDescriptionLength} characters.");
        }

        return trimmed;
    }

    private static decimal ValidatePrice(decimal price)
    {
        if (price < 0m)
        {
            throw DomainException.Validation("Product base price cannot be negative.");
        }

        return price;
    }
}
