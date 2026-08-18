using Catalog;
using Catalog.Domain;
using Knight.Application.Exceptions;
using Knight.Contracts.Catalog;

namespace Knight.Api.Endpoints;

/// <summary>
/// Shared entity-to-contract projections and request parsing for every catalog
/// endpoint group. The catalog feature gate is no longer written here: it is
/// declared on each route group with
/// <c>Knight.Api.Authorization.FeatureGateEndpointExtensions.RequireFeature</c>,
/// so a newly added route inherits the gate from its group instead of relying on
/// the author remembering a first statement.
/// </summary>
internal static class CatalogEndpointSupport
{
    internal static CategoryResponse ToResponse(Category category) => new()
    {
        Id = category.Id,
        Name = category.Name,
        Slug = category.Slug,
        Description = category.Description,
        IsVisible = category.IsVisible,
        DisplayOrder = category.DisplayOrder,
        CreatedAt = category.CreatedAt,
        UpdatedAt = category.UpdatedAt
    };

    internal static ProductResponse ToResponse(Product product) => new()
    {
        Id = product.Id,
        CategoryId = product.CategoryId,
        Name = product.Name,
        Slug = product.Slug,
        Description = product.Description,
        Status = product.Status.ToString(),
        BasePrice = product.BasePrice,
        IsVisible = product.IsVisible,
        IsAvailable = product.IsAvailable,
        DisplayOrder = product.DisplayOrder,
        CreatedAt = product.CreatedAt,
        UpdatedAt = product.UpdatedAt
    };

    internal static ProductVariantResponse ToResponse(ProductVariant variant) => new()
    {
        Id = variant.Id,
        ProductId = variant.ProductId,
        Name = variant.Name,
        Sku = variant.Sku,
        Price = variant.Price,
        CompareAtPrice = variant.CompareAtPrice,
        IsDefault = variant.IsDefault,
        IsAvailable = variant.IsAvailable,
        DisplayOrder = variant.DisplayOrder,
        CreatedAt = variant.CreatedAt,
        UpdatedAt = variant.UpdatedAt
    };

    internal static ModifierGroupResponse ToResponse(ModifierGroup group) => new()
    {
        Id = group.Id,
        Name = group.Name,
        IsRequired = group.IsRequired,
        MinSelections = group.MinSelections,
        MaxSelections = group.MaxSelections,
        DisplayOrder = group.DisplayOrder,
        CreatedAt = group.CreatedAt,
        UpdatedAt = group.UpdatedAt
    };

    internal static ModifierResponse ToResponse(Modifier modifier) => new()
    {
        Id = modifier.Id,
        ModifierGroupId = modifier.ModifierGroupId,
        Name = modifier.Name,
        PriceDelta = modifier.PriceDelta,
        IsAvailable = modifier.IsAvailable,
        DisplayOrder = modifier.DisplayOrder,
        CreatedAt = modifier.CreatedAt,
        UpdatedAt = modifier.UpdatedAt
    };

    internal static ProductMediaResponse ToResponse(ProductMedia media) => new()
    {
        Id = media.Id,
        ProductId = media.ProductId,
        StorageKey = media.StorageKey,
        AltText = media.AltText,
        DisplayOrder = media.DisplayOrder,
        IsPrimary = media.IsPrimary,
        CreatedAt = media.CreatedAt
    };

    internal static CreateCategoryInput ToInput(CreateCategoryRequest request) =>
        new(request.Name, request.Slug, request.Description, request.IsVisible, request.DisplayOrder);

    internal static UpdateCategoryInput ToInput(UpdateCategoryRequest request) =>
        new(request.Name, request.Slug, request.Description, request.IsVisible, request.DisplayOrder);

    /// <summary>
    /// New products always enter the catalog as drafts; publishing is the
    /// explicit activate operation, never a side effect of creation.
    /// </summary>
    internal static CreateProductInput ToInput(CreateProductRequest request) =>
        new(
            request.CategoryId,
            request.Name,
            request.Slug,
            request.Description,
            ProductStatus.Draft,
            request.BasePrice,
            request.IsVisible,
            request.IsAvailable,
            request.DisplayOrder);

    internal static UpdateProductInput ToInput(UpdateProductRequest request) =>
        new(
            request.Name,
            request.Slug,
            request.Description,
            request.BasePrice,
            request.IsVisible,
            request.IsAvailable,
            request.DisplayOrder);

    internal static CreateProductVariantInput ToInput(CreateProductVariantRequest request) =>
        new(request.Name, request.Sku, request.Price, request.CompareAtPrice, request.IsAvailable, request.DisplayOrder);

    internal static UpdateProductVariantInput ToInput(UpdateProductVariantRequest request) =>
        new(request.Name, request.Sku, request.Price, request.CompareAtPrice, request.IsAvailable, request.DisplayOrder);

    internal static CreateModifierGroupInput ToInput(CreateModifierGroupRequest request) =>
        new(request.Name, request.IsRequired, request.MinSelections, request.MaxSelections, request.DisplayOrder);

    internal static UpdateModifierGroupInput ToInput(UpdateModifierGroupRequest request) =>
        new(request.Name, request.IsRequired, request.MinSelections, request.MaxSelections, request.DisplayOrder);

    internal static CreateModifierInput ToInput(CreateModifierRequest request) =>
        new(request.Name, request.PriceDelta, request.IsAvailable, request.DisplayOrder);

    internal static UpdateModifierInput ToInput(UpdateModifierRequest request) =>
        new(request.Name, request.PriceDelta, request.IsAvailable, request.DisplayOrder);

    internal static AddProductMediaInput ToInput(AddProductMediaRequest request) =>
        new(request.StorageKey, request.AltText, request.DisplayOrder, request.IsPrimary);

    internal static IReadOnlyCollection<ProductModifierGroupAssignment> ToAssignments(ReplaceProductModifierGroupsRequest request) =>
        request.Assignments
            .Select(a => new ProductModifierGroupAssignment(a.ModifierGroupId, a.DisplayOrder))
            .ToArray();

    /// <summary>
    /// Parses the optional product-status query filter, rejecting unknown values
    /// rather than silently ignoring them.
    /// </summary>
    internal static ProductStatus? ParseStatus(string? status)
    {
        if (string.IsNullOrWhiteSpace(status))
        {
            return null;
        }

        if (!Enum.TryParse<ProductStatus>(status, ignoreCase: true, out var parsed))
        {
            throw new ValidationException(new Dictionary<string, string[]>
            {
                ["status"] = [$"'{status}' is not a valid product status."]
            });
        }

        return parsed;
    }

    /// <summary>
    /// Resolves modifier-group assignments into a name-bearing response by
    /// looking each group up through the same tenant-scoped service, so an
    /// assignment pointing outside the tenant can never leak a name.
    /// </summary>
    internal static async Task<IReadOnlyCollection<ProductModifierGroupResponse>> ToAssignmentResponsesAsync(
        IReadOnlyCollection<ProductModifierGroup> assignments,
        IModifierGroupManagementService modifierGroupService,
        Guid tenantId,
        CancellationToken cancellationToken)
    {
        var responses = new List<ProductModifierGroupResponse>(assignments.Count);
        foreach (var assignment in assignments.OrderBy(a => a.DisplayOrder))
        {
            var group = await modifierGroupService.GetAsync(tenantId, assignment.ModifierGroupId, cancellationToken);
            if (group is null)
            {
                continue;
            }

            responses.Add(new ProductModifierGroupResponse
            {
                ModifierGroupId = assignment.ModifierGroupId,
                Name = group.Name,
                DisplayOrder = assignment.DisplayOrder
            });
        }

        return responses;
    }
}
