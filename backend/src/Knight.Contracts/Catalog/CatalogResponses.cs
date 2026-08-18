namespace Knight.Contracts.Catalog;

/// <summary>
/// Administration-facing catalog responses. These are only ever returned on
/// authorized tenant-admin or platform-admin routes, so they may carry
/// lifecycle and audit fields that the storefront must never see — the
/// storefront shapes live in <c>CatalogPublicResponses.cs</c>.
/// </summary>
public sealed record CategoryResponse
{
    public required Guid Id { get; init; }

    public required string Name { get; init; }

    public required string Slug { get; init; }

    public string? Description { get; init; }

    public required bool IsVisible { get; init; }

    public required int DisplayOrder { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset? UpdatedAt { get; init; }
}

public sealed record ProductResponse
{
    public required Guid Id { get; init; }

    public required Guid CategoryId { get; init; }

    public required string Name { get; init; }

    public required string Slug { get; init; }

    public string? Description { get; init; }

    /// <summary>Lifecycle state as its string name (Draft, Active, Archived).</summary>
    public required string Status { get; init; }

    public required decimal BasePrice { get; init; }

    public required bool IsVisible { get; init; }

    public required bool IsAvailable { get; init; }

    public required int DisplayOrder { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset? UpdatedAt { get; init; }
}

public sealed record ProductVariantResponse
{
    public required Guid Id { get; init; }

    public required Guid ProductId { get; init; }

    public required string Name { get; init; }

    public string? Sku { get; init; }

    public required decimal Price { get; init; }

    public decimal? CompareAtPrice { get; init; }

    public required bool IsDefault { get; init; }

    public required bool IsAvailable { get; init; }

    public required int DisplayOrder { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset? UpdatedAt { get; init; }
}

public sealed record ModifierGroupResponse
{
    public required Guid Id { get; init; }

    public required string Name { get; init; }

    public required bool IsRequired { get; init; }

    public required int MinSelections { get; init; }

    public required int MaxSelections { get; init; }

    public required int DisplayOrder { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset? UpdatedAt { get; init; }
}

public sealed record ModifierResponse
{
    public required Guid Id { get; init; }

    public required Guid ModifierGroupId { get; init; }

    public required string Name { get; init; }

    public required decimal PriceDelta { get; init; }

    public required bool IsAvailable { get; init; }

    public required int DisplayOrder { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset? UpdatedAt { get; init; }
}

/// <summary>
/// One modifier group as assigned to a product, with the group's name joined in
/// so the caller does not have to resolve every identifier separately.
/// </summary>
public sealed record ProductModifierGroupResponse
{
    public required Guid ModifierGroupId { get; init; }

    public required string Name { get; init; }

    public required int DisplayOrder { get; init; }
}

public sealed record ProductMediaResponse
{
    public required Guid Id { get; init; }

    public required Guid ProductId { get; init; }

    public required string StorageKey { get; init; }

    public string? AltText { get; init; }

    public required int DisplayOrder { get; init; }

    public required bool IsPrimary { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }
}
