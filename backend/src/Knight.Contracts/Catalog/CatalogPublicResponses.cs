namespace Knight.Contracts.Catalog;

/// <summary>
/// Storefront-facing catalog responses. Deliberately distinct from the
/// administration shapes: no audit timestamps, no lifecycle status, no internal
/// identifiers such as SKU, and no visibility flags — everything returned on a
/// public route is visible by definition. Keeping these separate means a future
/// change to an admin response can never widen the anonymous surface by accident.
/// </summary>
public sealed record PublicCategoryResponse
{
    public required Guid Id { get; init; }

    public required string Name { get; init; }

    public required string Slug { get; init; }

    public string? Description { get; init; }

    public required int DisplayOrder { get; init; }
}

/// <summary>Primary image reference for a storefront listing.</summary>
public sealed record PublicMediaResponse
{
    public required string StorageKey { get; init; }

    public string? AltText { get; init; }

    public required bool IsPrimary { get; init; }
}

public sealed record PublicVariantResponse
{
    public required Guid Id { get; init; }

    public required string Name { get; init; }

    public required decimal Price { get; init; }

    public decimal? CompareAtPrice { get; init; }

    public required bool IsAvailable { get; init; }
}

public sealed record PublicModifierResponse
{
    public required Guid Id { get; init; }

    public required string Name { get; init; }

    public required decimal PriceDelta { get; init; }

    public required bool IsAvailable { get; init; }
}

public sealed record PublicModifierGroupResponse
{
    public required Guid Id { get; init; }

    public required string Name { get; init; }

    public required bool IsRequired { get; init; }

    public required int MinSelections { get; init; }

    public required int MaxSelections { get; init; }

    public required IReadOnlyCollection<PublicModifierResponse> Modifiers { get; init; } = [];
}

/// <summary>
/// Listing shape. <see cref="BasePrice"/> is only populated when it is the
/// authoritative price for the product — when <see cref="HasVariants"/> is true
/// the effective price comes from the variants, so the base price is suppressed
/// rather than shown as a misleading number.
/// </summary>
public sealed record PublicProductSummaryResponse
{
    public required Guid Id { get; init; }

    public required string Name { get; init; }

    public required string Slug { get; init; }

    public string? Description { get; init; }

    public required Guid CategoryId { get; init; }

    public required bool HasVariants { get; init; }

    public decimal? BasePrice { get; init; }

    /// <summary>ISO currency code the prices in this response are denominated in.</summary>
    public required string Currency { get; init; }

    public required bool IsAvailable { get; init; }

    public PublicMediaResponse? PrimaryMedia { get; init; }
}

public sealed record PublicProductDetailResponse
{
    public required Guid Id { get; init; }

    public required string Name { get; init; }

    public required string Slug { get; init; }

    public string? Description { get; init; }

    public required Guid CategoryId { get; init; }

    public required bool HasVariants { get; init; }

    public decimal? BasePrice { get; init; }

    /// <summary>ISO currency code the prices in this response are denominated in.</summary>
    public required string Currency { get; init; }

    public required bool IsAvailable { get; init; }

    public required IReadOnlyCollection<PublicVariantResponse> Variants { get; init; } = [];

    public required IReadOnlyCollection<PublicModifierGroupResponse> ModifierGroups { get; init; } = [];

    public required IReadOnlyCollection<PublicMediaResponse> Media { get; init; } = [];
}
