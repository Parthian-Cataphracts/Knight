namespace Knight.Contracts.Catalog;

/// <summary>
/// Request payloads for the tenant-administration and platform-administration
/// catalog surfaces. Each record maps 1:1 onto the corresponding application
/// service input record so endpoint handlers stay free of business logic.
/// </summary>
public sealed record CreateCategoryRequest
{
    public required string Name { get; init; }

    public string? Slug { get; init; }

    public string? Description { get; init; }

    public bool IsVisible { get; init; } = true;

    public int DisplayOrder { get; init; }
}

public sealed record UpdateCategoryRequest
{
    public required string Name { get; init; }

    public string? Slug { get; init; }

    public string? Description { get; init; }

    public bool IsVisible { get; init; } = true;

    public int DisplayOrder { get; init; }
}

public sealed record CreateProductRequest
{
    public required Guid CategoryId { get; init; }

    public required string Name { get; init; }

    public string? Slug { get; init; }

    public string? Description { get; init; }

    public required decimal BasePrice { get; init; }

    public bool IsVisible { get; init; } = true;

    public bool IsAvailable { get; init; } = true;

    public int DisplayOrder { get; init; }
}

public sealed record UpdateProductRequest
{
    public required string Name { get; init; }

    public string? Slug { get; init; }

    public string? Description { get; init; }

    public required decimal BasePrice { get; init; }

    public bool IsVisible { get; init; }

    public bool IsAvailable { get; init; }

    public int DisplayOrder { get; init; }
}

public sealed record ChangeProductCategoryRequest
{
    public required Guid CategoryId { get; init; }
}

public sealed record SetVisibilityRequest
{
    public required bool IsVisible { get; init; }
}

public sealed record SetAvailabilityRequest
{
    public required bool IsAvailable { get; init; }
}

public sealed record CreateProductVariantRequest
{
    public required string Name { get; init; }

    public string? Sku { get; init; }

    public required decimal Price { get; init; }

    public decimal? CompareAtPrice { get; init; }

    public bool IsAvailable { get; init; } = true;

    public int DisplayOrder { get; init; }
}

public sealed record UpdateProductVariantRequest
{
    public required string Name { get; init; }

    public string? Sku { get; init; }

    public required decimal Price { get; init; }

    public decimal? CompareAtPrice { get; init; }

    public bool IsAvailable { get; init; }

    public int DisplayOrder { get; init; }
}

public sealed record CreateModifierGroupRequest
{
    public required string Name { get; init; }

    public bool IsRequired { get; init; }

    public int MinSelections { get; init; }

    public int MaxSelections { get; init; }

    public int DisplayOrder { get; init; }
}

public sealed record UpdateModifierGroupRequest
{
    public required string Name { get; init; }

    public bool IsRequired { get; init; }

    public int MinSelections { get; init; }

    public int MaxSelections { get; init; }

    public int DisplayOrder { get; init; }
}

public sealed record CreateModifierRequest
{
    public required string Name { get; init; }

    public decimal PriceDelta { get; init; }

    public bool IsAvailable { get; init; } = true;

    public int DisplayOrder { get; init; }
}

public sealed record UpdateModifierRequest
{
    public required string Name { get; init; }

    public decimal PriceDelta { get; init; }

    public bool IsAvailable { get; init; }

    public int DisplayOrder { get; init; }
}

public sealed record ProductModifierGroupAssignmentRequest
{
    public required Guid ModifierGroupId { get; init; }

    public int DisplayOrder { get; init; }
}

/// <summary>
/// Replace-all semantics: the supplied set becomes the product's complete
/// modifier-group assignment list. An empty collection clears every assignment.
/// </summary>
public sealed record ReplaceProductModifierGroupsRequest
{
    public required IReadOnlyCollection<ProductModifierGroupAssignmentRequest> Assignments { get; init; } = [];
}

public sealed record AddProductMediaRequest
{
    public required string StorageKey { get; init; }

    public string? AltText { get; init; }

    public int DisplayOrder { get; init; }

    public bool IsPrimary { get; init; }
}
