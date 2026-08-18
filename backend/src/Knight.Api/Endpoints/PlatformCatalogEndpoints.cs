using Catalog;
using Knight.Api.Authorization;
using Knight.Contracts.Catalog;
using Knight.Contracts.Common;

namespace Knight.Api.Endpoints;

/// <summary>
/// Platform-authorized catalog management for a named tenant. The target tenant
/// comes from the route rather than from <c>ITenantContext</c>, and the underlying
/// application services are exactly the ones tenant self-administration uses.
///
/// Consistent with the other platform surfaces, the <c>PlatformAdminOnly</c>
/// policy is the whole authorization boundary — no per-route permission checks.
/// The catalog feature gate is still enforced for the target tenant: platform
/// access decides who may act, not which capabilities a tenant has bought.
/// </summary>
public static class PlatformCatalogEndpoints
{
    public static void MapPlatformCatalogEndpoints(this IEndpointRouteBuilder endpoints)
    {
        MapCategories(endpoints);
        MapProducts(endpoints);
        MapVariants(endpoints);
        MapModifierGroups(endpoints);
        MapModifiers(endpoints);
        MapMedia(endpoints);
    }

    private static RouteGroupBuilder MapPlatformGroup(IEndpointRouteBuilder endpoints, string pattern, string tag) =>
        endpoints.MapGroup(pattern)
            .RequireAuthorization("PlatformAdminOnly")
            .RequireFeatureForRouteTenant(CatalogFeature.Key)
            .RequireRateLimiting("platform")
            .WithTags(tag);

    private static void MapCategories(IEndpointRouteBuilder endpoints)
    {
        var group = MapPlatformGroup(endpoints, "/api/platform/tenants/{tenantId:guid}/catalog/categories", "Platform Tenant Catalog Categories");

        group.MapGet("/", async (
            Guid tenantId,
            int? page,
            int? pageSize,
            string? search,
            bool? isVisible,
            ICategoryManagementService service,
            CancellationToken cancellationToken) =>
        {
            var result = await service.ListAsync(tenantId, page ?? 1, pageSize ?? 20, search, isVisible, cancellationToken);
            var items = result.Items.Select(CatalogEndpointSupport.ToResponse).ToArray();
            return Results.Ok(PagedResponse<CategoryResponse>.Create(items, result.Page, result.PageSize, result.TotalCount));
        });

        group.MapPost("/", async (
            Guid tenantId,
            CreateCategoryRequest request,
            ICategoryManagementService service,
            CancellationToken cancellationToken) =>
        {
            var category = await service.CreateAsync(tenantId, CatalogEndpointSupport.ToInput(request), cancellationToken);
            return Results.Created(
                $"/api/platform/tenants/{tenantId}/catalog/categories/{category.Id}",
                CatalogEndpointSupport.ToResponse(category));
        });

        group.MapGet("/{categoryId:guid}", async (
            Guid tenantId,
            Guid categoryId,
            ICategoryManagementService service,
            CancellationToken cancellationToken) =>
        {
            var category = await service.GetAsync(tenantId, categoryId, cancellationToken);
            return category is null ? Results.NotFound() : Results.Ok(CatalogEndpointSupport.ToResponse(category));
        });

        group.MapPut("/{categoryId:guid}", async (
            Guid tenantId,
            Guid categoryId,
            UpdateCategoryRequest request,
            ICategoryManagementService service,
            CancellationToken cancellationToken) =>
        {
            var category = await service.UpdateAsync(tenantId, categoryId, CatalogEndpointSupport.ToInput(request), cancellationToken);
            return Results.Ok(CatalogEndpointSupport.ToResponse(category));
        });

        group.MapPut("/{categoryId:guid}/visibility", async (
            Guid tenantId,
            Guid categoryId,
            SetVisibilityRequest request,
            ICategoryManagementService service,
            CancellationToken cancellationToken) =>
        {
            var category = await service.SetVisibilityAsync(tenantId, categoryId, request.IsVisible, cancellationToken);
            return Results.Ok(CatalogEndpointSupport.ToResponse(category));
        });

        group.MapDelete("/{categoryId:guid}", async (
            Guid tenantId,
            Guid categoryId,
            ICategoryManagementService service,
            CancellationToken cancellationToken) =>
        {
            await service.DeleteAsync(tenantId, categoryId, cancellationToken);
            return Results.NoContent();
        });
    }

    private static void MapProducts(IEndpointRouteBuilder endpoints)
    {
        var group = MapPlatformGroup(endpoints, "/api/platform/tenants/{tenantId:guid}/catalog/products", "Platform Tenant Catalog Products");

        group.MapGet("/", async (
            Guid tenantId,
            int? page,
            int? pageSize,
            Guid? categoryId,
            string? status,
            bool? isVisible,
            bool? isAvailable,
            string? search,
            IProductManagementService service,
            CancellationToken cancellationToken) =>
        {
            var filter = new ProductListFilter(categoryId, CatalogEndpointSupport.ParseStatus(status), isVisible, isAvailable, search);
            var result = await service.ListAsync(tenantId, page ?? 1, pageSize ?? 20, filter, cancellationToken);
            var items = result.Items.Select(CatalogEndpointSupport.ToResponse).ToArray();
            return Results.Ok(PagedResponse<ProductResponse>.Create(items, result.Page, result.PageSize, result.TotalCount));
        });

        group.MapPost("/", async (
            Guid tenantId,
            CreateProductRequest request,
            IProductManagementService service,
            CancellationToken cancellationToken) =>
        {
            var product = await service.CreateAsync(tenantId, CatalogEndpointSupport.ToInput(request), cancellationToken);
            return Results.Created(
                $"/api/platform/tenants/{tenantId}/catalog/products/{product.Id}",
                CatalogEndpointSupport.ToResponse(product));
        });

        group.MapGet("/{productId:guid}", async (
            Guid tenantId,
            Guid productId,
            IProductManagementService service,
            CancellationToken cancellationToken) =>
        {
            var product = await service.GetAsync(tenantId, productId, cancellationToken);
            return product is null ? Results.NotFound() : Results.Ok(CatalogEndpointSupport.ToResponse(product));
        });

        group.MapPut("/{productId:guid}", async (
            Guid tenantId,
            Guid productId,
            UpdateProductRequest request,
            IProductManagementService service,
            CancellationToken cancellationToken) =>
        {
            var product = await service.UpdateAsync(tenantId, productId, CatalogEndpointSupport.ToInput(request), cancellationToken);
            return Results.Ok(CatalogEndpointSupport.ToResponse(product));
        });

        group.MapPut("/{productId:guid}/category", async (
            Guid tenantId,
            Guid productId,
            ChangeProductCategoryRequest request,
            IProductManagementService service,
            CancellationToken cancellationToken) =>
        {
            var product = await service.ChangeCategoryAsync(tenantId, productId, request.CategoryId, cancellationToken);
            return Results.Ok(CatalogEndpointSupport.ToResponse(product));
        });

        group.MapPost("/{productId:guid}/activate", async (
            Guid tenantId,
            Guid productId,
            IProductManagementService service,
            CancellationToken cancellationToken) =>
        {
            var product = await service.ActivateAsync(tenantId, productId, cancellationToken);
            return Results.Ok(CatalogEndpointSupport.ToResponse(product));
        });

        group.MapPut("/{productId:guid}/visibility", async (
            Guid tenantId,
            Guid productId,
            SetVisibilityRequest request,
            IProductManagementService service,
            CancellationToken cancellationToken) =>
        {
            var product = await service.SetVisibilityAsync(tenantId, productId, request.IsVisible, cancellationToken);
            return Results.Ok(CatalogEndpointSupport.ToResponse(product));
        });

        group.MapPut("/{productId:guid}/availability", async (
            Guid tenantId,
            Guid productId,
            SetAvailabilityRequest request,
            IProductManagementService service,
            CancellationToken cancellationToken) =>
        {
            var product = await service.SetAvailabilityAsync(tenantId, productId, request.IsAvailable, cancellationToken);
            return Results.Ok(CatalogEndpointSupport.ToResponse(product));
        });

        group.MapDelete("/{productId:guid}", async (
            Guid tenantId,
            Guid productId,
            IProductManagementService service,
            CancellationToken cancellationToken) =>
        {
            var product = await service.ArchiveAsync(tenantId, productId, cancellationToken);
            return Results.Ok(CatalogEndpointSupport.ToResponse(product));
        });

        group.MapGet("/{productId:guid}/modifier-groups", async (
            Guid tenantId,
            Guid productId,
            IProductModifierAssignmentService service,
            IModifierGroupManagementService modifierGroupService,
            CancellationToken cancellationToken) =>
        {
            var assignments = await service.ListAsync(tenantId, productId, cancellationToken);
            return Results.Ok(await CatalogEndpointSupport.ToAssignmentResponsesAsync(assignments, modifierGroupService, tenantId, cancellationToken));
        });

        group.MapPut("/{productId:guid}/modifier-groups", async (
            Guid tenantId,
            Guid productId,
            ReplaceProductModifierGroupsRequest request,
            IProductModifierAssignmentService service,
            IModifierGroupManagementService modifierGroupService,
            CancellationToken cancellationToken) =>
        {
            await service.ReplaceAsync(tenantId, productId, CatalogEndpointSupport.ToAssignments(request), cancellationToken);
            var assignments = await service.ListAsync(tenantId, productId, cancellationToken);
            return Results.Ok(await CatalogEndpointSupport.ToAssignmentResponsesAsync(assignments, modifierGroupService, tenantId, cancellationToken));
        });
    }

    private static void MapVariants(IEndpointRouteBuilder endpoints)
    {
        var group = MapPlatformGroup(
            endpoints,
            "/api/platform/tenants/{tenantId:guid}/catalog/products/{productId:guid}/variants",
            "Platform Tenant Catalog Variants");

        group.MapGet("/", async (
            Guid tenantId,
            Guid productId,
            IProductVariantManagementService service,
            CancellationToken cancellationToken) =>
        {
            var variants = await service.ListAsync(tenantId, productId, cancellationToken);
            return Results.Ok(variants.Select(CatalogEndpointSupport.ToResponse).ToArray());
        });

        group.MapPost("/", async (
            Guid tenantId,
            Guid productId,
            CreateProductVariantRequest request,
            IProductVariantManagementService service,
            CancellationToken cancellationToken) =>
        {
            var variant = await service.CreateAsync(tenantId, productId, CatalogEndpointSupport.ToInput(request), cancellationToken);
            return Results.Created(
                $"/api/platform/tenants/{tenantId}/catalog/products/{productId}/variants/{variant.Id}",
                CatalogEndpointSupport.ToResponse(variant));
        });

        group.MapGet("/{variantId:guid}", async (
            Guid tenantId,
            Guid productId,
            Guid variantId,
            IProductVariantManagementService service,
            CancellationToken cancellationToken) =>
        {
            var variant = await service.GetAsync(tenantId, productId, variantId, cancellationToken);
            return variant is null ? Results.NotFound() : Results.Ok(CatalogEndpointSupport.ToResponse(variant));
        });

        group.MapPut("/{variantId:guid}", async (
            Guid tenantId,
            Guid productId,
            Guid variantId,
            UpdateProductVariantRequest request,
            IProductVariantManagementService service,
            CancellationToken cancellationToken) =>
        {
            var variant = await service.UpdateAsync(tenantId, productId, variantId, CatalogEndpointSupport.ToInput(request), cancellationToken);
            return Results.Ok(CatalogEndpointSupport.ToResponse(variant));
        });

        group.MapPost("/{variantId:guid}/default", async (
            Guid tenantId,
            Guid productId,
            Guid variantId,
            IProductVariantManagementService service,
            CancellationToken cancellationToken) =>
        {
            await service.SetDefaultAsync(tenantId, productId, variantId, cancellationToken);
            return Results.NoContent();
        });

        group.MapPut("/{variantId:guid}/availability", async (
            Guid tenantId,
            Guid productId,
            Guid variantId,
            SetAvailabilityRequest request,
            IProductVariantManagementService service,
            CancellationToken cancellationToken) =>
        {
            var variant = await service.SetAvailabilityAsync(tenantId, productId, variantId, request.IsAvailable, cancellationToken);
            return Results.Ok(CatalogEndpointSupport.ToResponse(variant));
        });

        group.MapDelete("/{variantId:guid}", async (
            Guid tenantId,
            Guid productId,
            Guid variantId,
            IProductVariantManagementService service,
            CancellationToken cancellationToken) =>
        {
            await service.DeactivateAsync(tenantId, productId, variantId, cancellationToken);
            return Results.NoContent();
        });
    }

    private static void MapModifierGroups(IEndpointRouteBuilder endpoints)
    {
        var group = MapPlatformGroup(
            endpoints,
            "/api/platform/tenants/{tenantId:guid}/catalog/modifier-groups",
            "Platform Tenant Catalog Modifiers");

        group.MapGet("/", async (
            Guid tenantId,
            int? page,
            int? pageSize,
            string? search,
            IModifierGroupManagementService service,
            CancellationToken cancellationToken) =>
        {
            var result = await service.ListAsync(tenantId, page ?? 1, pageSize ?? 20, search, cancellationToken);
            var items = result.Items.Select(CatalogEndpointSupport.ToResponse).ToArray();
            return Results.Ok(PagedResponse<ModifierGroupResponse>.Create(items, result.Page, result.PageSize, result.TotalCount));
        });

        group.MapPost("/", async (
            Guid tenantId,
            CreateModifierGroupRequest request,
            IModifierGroupManagementService service,
            CancellationToken cancellationToken) =>
        {
            var modifierGroup = await service.CreateAsync(tenantId, CatalogEndpointSupport.ToInput(request), cancellationToken);
            return Results.Created(
                $"/api/platform/tenants/{tenantId}/catalog/modifier-groups/{modifierGroup.Id}",
                CatalogEndpointSupport.ToResponse(modifierGroup));
        });

        group.MapGet("/{groupId:guid}", async (
            Guid tenantId,
            Guid groupId,
            IModifierGroupManagementService service,
            CancellationToken cancellationToken) =>
        {
            var modifierGroup = await service.GetAsync(tenantId, groupId, cancellationToken);
            return modifierGroup is null ? Results.NotFound() : Results.Ok(CatalogEndpointSupport.ToResponse(modifierGroup));
        });

        group.MapPut("/{groupId:guid}", async (
            Guid tenantId,
            Guid groupId,
            UpdateModifierGroupRequest request,
            IModifierGroupManagementService service,
            CancellationToken cancellationToken) =>
        {
            var modifierGroup = await service.UpdateAsync(tenantId, groupId, CatalogEndpointSupport.ToInput(request), cancellationToken);
            return Results.Ok(CatalogEndpointSupport.ToResponse(modifierGroup));
        });

        group.MapDelete("/{groupId:guid}", async (
            Guid tenantId,
            Guid groupId,
            IModifierGroupManagementService service,
            CancellationToken cancellationToken) =>
        {
            await service.DeleteAsync(tenantId, groupId, cancellationToken);
            return Results.NoContent();
        });
    }

    private static void MapModifiers(IEndpointRouteBuilder endpoints)
    {
        var group = MapPlatformGroup(
            endpoints,
            "/api/platform/tenants/{tenantId:guid}/catalog/modifier-groups/{groupId:guid}/modifiers",
            "Platform Tenant Catalog Modifiers");

        group.MapGet("/", async (
            Guid tenantId,
            Guid groupId,
            IModifierGroupManagementService service,
            CancellationToken cancellationToken) =>
        {
            var items = await service.ListModifiersAsync(tenantId, groupId, cancellationToken);
            return Results.Ok(items.Select(CatalogEndpointSupport.ToResponse).ToArray());
        });

        group.MapPost("/", async (
            Guid tenantId,
            Guid groupId,
            CreateModifierRequest request,
            IModifierGroupManagementService service,
            CancellationToken cancellationToken) =>
        {
            var modifier = await service.CreateModifierAsync(tenantId, groupId, CatalogEndpointSupport.ToInput(request), cancellationToken);
            return Results.Created(
                $"/api/platform/tenants/{tenantId}/catalog/modifier-groups/{groupId}/modifiers/{modifier.Id}",
                CatalogEndpointSupport.ToResponse(modifier));
        });

        group.MapGet("/{modifierId:guid}", async (
            Guid tenantId,
            Guid groupId,
            Guid modifierId,
            IModifierGroupManagementService service,
            CancellationToken cancellationToken) =>
        {
            var modifier = await service.GetModifierAsync(tenantId, groupId, modifierId, cancellationToken);
            return modifier is null ? Results.NotFound() : Results.Ok(CatalogEndpointSupport.ToResponse(modifier));
        });

        group.MapPut("/{modifierId:guid}", async (
            Guid tenantId,
            Guid groupId,
            Guid modifierId,
            UpdateModifierRequest request,
            IModifierGroupManagementService service,
            CancellationToken cancellationToken) =>
        {
            var modifier = await service.UpdateModifierAsync(tenantId, groupId, modifierId, CatalogEndpointSupport.ToInput(request), cancellationToken);
            return Results.Ok(CatalogEndpointSupport.ToResponse(modifier));
        });

        group.MapPut("/{modifierId:guid}/availability", async (
            Guid tenantId,
            Guid groupId,
            Guid modifierId,
            SetAvailabilityRequest request,
            IModifierGroupManagementService service,
            CancellationToken cancellationToken) =>
        {
            var modifier = await service.SetModifierAvailabilityAsync(tenantId, groupId, modifierId, request.IsAvailable, cancellationToken);
            return Results.Ok(CatalogEndpointSupport.ToResponse(modifier));
        });
    }

    private static void MapMedia(IEndpointRouteBuilder endpoints)
    {
        var group = MapPlatformGroup(
            endpoints,
            "/api/platform/tenants/{tenantId:guid}/catalog/products/{productId:guid}/media",
            "Platform Tenant Catalog Media");

        group.MapGet("/", async (
            Guid tenantId,
            Guid productId,
            IProductMediaManagementService service,
            CancellationToken cancellationToken) =>
        {
            var media = await service.ListAsync(tenantId, productId, cancellationToken);
            return Results.Ok(media.Select(CatalogEndpointSupport.ToResponse).ToArray());
        });

        group.MapPost("/", async (
            Guid tenantId,
            Guid productId,
            AddProductMediaRequest request,
            IProductMediaManagementService service,
            CancellationToken cancellationToken) =>
        {
            var media = await service.AddAsync(tenantId, productId, CatalogEndpointSupport.ToInput(request), cancellationToken);
            return Results.Created(
                $"/api/platform/tenants/{tenantId}/catalog/products/{productId}/media/{media.Id}",
                CatalogEndpointSupport.ToResponse(media));
        });

        group.MapPost("/{mediaId:guid}/primary", async (
            Guid tenantId,
            Guid productId,
            Guid mediaId,
            IProductMediaManagementService service,
            CancellationToken cancellationToken) =>
        {
            await service.SetPrimaryAsync(tenantId, productId, mediaId, cancellationToken);
            return Results.NoContent();
        });

        group.MapDelete("/{mediaId:guid}", async (
            Guid tenantId,
            Guid productId,
            Guid mediaId,
            IProductMediaManagementService service,
            CancellationToken cancellationToken) =>
        {
            await service.DeleteAsync(tenantId, productId, mediaId, cancellationToken);
            return Results.NoContent();
        });
    }
}
