using Knight.Domain.Common;
using Knight.Domain.Exceptions;

namespace Catalog.Domain;

/// <summary>
/// A media asset attached to a <see cref="Product"/>. <see cref="StorageKey"/> is
/// an object-store key, never a filesystem path — the "single primary image per
/// product" invariant is enforced transactionally in the persistence layer.
/// </summary>
public sealed class ProductMedia : Entity, ITenantScoped
{
    private const int MaxStorageKeyLength = 500;
    private const int MaxAltTextLength = 300;

    public Guid TenantId { get; private set; }

    public Guid ProductId { get; private set; }

    public string StorageKey { get; private set; }

    public string? AltText { get; private set; }

    public int DisplayOrder { get; private set; }

    public bool IsPrimary { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    private ProductMedia()
    {
        StorageKey = string.Empty;
    }

    private ProductMedia(
        Guid id,
        DateTimeOffset createdAt,
        Guid tenantId,
        Guid productId,
        string storageKey,
        string? altText,
        int displayOrder,
        bool isPrimary)
        : base(id)
    {
        TenantId = tenantId;
        ProductId = productId;
        StorageKey = storageKey;
        AltText = altText;
        DisplayOrder = displayOrder;
        IsPrimary = isPrimary;
        CreatedAt = createdAt;
    }

    public static ProductMedia Create(
        Guid id,
        DateTimeOffset createdAt,
        Guid tenantId,
        Guid productId,
        string storageKey,
        string? altText,
        int displayOrder,
        bool isPrimary)
    {
        if (tenantId == Guid.Empty || productId == Guid.Empty)
        {
            throw DomainException.Validation("Product media must reference a tenant and a product.");
        }

        return new ProductMedia(
            id,
            createdAt,
            tenantId,
            productId,
            ValidateStorageKey(storageKey),
            ValidateAltText(altText),
            displayOrder,
            isPrimary);
    }

    public void SetPrimary(bool isPrimary) => IsPrimary = isPrimary;

    private static string ValidateStorageKey(string storageKey)
    {
        if (string.IsNullOrWhiteSpace(storageKey))
        {
            throw DomainException.Validation("Product media storage key is required.");
        }

        var trimmed = storageKey.Trim();
        if (trimmed.Length > MaxStorageKeyLength)
        {
            throw DomainException.Validation($"Product media storage key cannot exceed {MaxStorageKeyLength} characters.");
        }

        // A storage key addresses an object in the configured object store. Values
        // that look like filesystem paths are rejected outright so a key can never
        // be reinterpreted as a local path by any downstream consumer.
        if (trimmed.Contains("..", StringComparison.Ordinal)
            || trimmed.StartsWith('/')
            || trimmed.StartsWith('\\')
            || (trimmed.Length >= 2 && char.IsAsciiLetter(trimmed[0]) && trimmed[1] == ':'))
        {
            throw DomainException.Validation("Product media storage key must not be a filesystem path.");
        }

        return trimmed;
    }

    private static string? ValidateAltText(string? altText)
    {
        if (string.IsNullOrWhiteSpace(altText))
        {
            return null;
        }

        var trimmed = altText.Trim();
        if (trimmed.Length > MaxAltTextLength)
        {
            throw DomainException.Validation($"Product media alt text cannot exceed {MaxAltTextLength} characters.");
        }

        return trimmed;
    }
}
