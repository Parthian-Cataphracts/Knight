using Knight.Domain.Common;
using Knight.Domain.Exceptions;

namespace Catalog.Domain;

/// <summary>
/// A tenant-scoped grouping of <see cref="Product"/> entries. <see cref="Slug"/> is
/// the stable, uniquely-indexed public identifier within a tenant; it is always
/// stored in normalized form so the unique index and lookups agree.
/// </summary>
public sealed class Category : AuditableEntity, ITenantScoped
{
    private const int MaxNameLength = 150;
    private const int MaxDescriptionLength = 1000;

    public Guid TenantId { get; private set; }

    public string Name { get; private set; }

    public string Slug { get; private set; }

    public string? Description { get; private set; }

    public bool IsVisible { get; private set; }

    public int DisplayOrder { get; private set; }

    private Category()
    {
        Name = string.Empty;
        Slug = string.Empty;
    }

    private Category(
        Guid id,
        DateTimeOffset createdAt,
        Guid tenantId,
        string name,
        string slug,
        string? description,
        bool isVisible,
        int displayOrder)
        : base(id, createdAt)
    {
        TenantId = tenantId;
        Name = name;
        Slug = slug;
        Description = description;
        IsVisible = isVisible;
        DisplayOrder = displayOrder;
    }

    public static Category Create(
        Guid id,
        DateTimeOffset now,
        Guid tenantId,
        string name,
        string? slug,
        string? description,
        bool isVisible,
        int displayOrder)
    {
        if (tenantId == Guid.Empty)
        {
            throw DomainException.Validation("A category must belong to a tenant.");
        }

        var validatedName = ValidateName(name);
        var normalizedSlug = NormalizeSlug(string.IsNullOrWhiteSpace(slug) ? validatedName : slug);

        return new Category(
            id,
            now,
            tenantId,
            validatedName,
            normalizedSlug,
            ValidateDescription(description),
            isVisible,
            displayOrder);
    }

    public static string NormalizeSlug(string value) => SlugNormalizer.Normalize(value);

    public void Rename(string name, DateTimeOffset now)
    {
        Name = ValidateName(name);
        MarkUpdated(now);
    }

    public void ChangeSlug(string slug, DateTimeOffset now)
    {
        Slug = NormalizeSlug(slug);
        MarkUpdated(now);
    }

    public void UpdateDetails(
        string name,
        string? slug,
        string? description,
        bool isVisible,
        int displayOrder,
        DateTimeOffset now)
    {
        var validatedName = ValidateName(name);

        Name = validatedName;
        Slug = NormalizeSlug(string.IsNullOrWhiteSpace(slug) ? validatedName : slug);
        Description = ValidateDescription(description);
        IsVisible = isVisible;
        DisplayOrder = displayOrder;
        MarkUpdated(now);
    }

    public void SetVisibility(bool isVisible, DateTimeOffset now)
    {
        IsVisible = isVisible;
        MarkUpdated(now);
    }

    private static string ValidateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw DomainException.Validation("Category name is required.");
        }

        var trimmed = name.Trim();
        if (trimmed.Length > MaxNameLength)
        {
            throw DomainException.Validation($"Category name cannot exceed {MaxNameLength} characters.");
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
            throw DomainException.Validation($"Category description cannot exceed {MaxDescriptionLength} characters.");
        }

        return trimmed;
    }
}
