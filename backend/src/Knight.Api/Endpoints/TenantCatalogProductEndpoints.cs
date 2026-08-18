using Catalog;
using Knight.Api.Authorization;
using Knight.Application.Abstractions.Tenancy;
using Knight.Contracts.Catalog;
using Knight.Contracts.Common;

namespace Knight.Api.Endpoints;

/// <summary>
/// Tenant self-administration of catalog products, including the product's
/// modifier-group assignments.
///
/// Permission split: editing what a product *is* — including whether it appears
/// in the storefront at all (visibility) — is content editing and requires
/// <c>catalog.products.update</c>. <c>catalog.availability.manage</c> is reserved
/// strictly for the day-to-day "can it be ordered right now" toggle, so a
/// front-of-house role can be granted availability control without ever gaining
/// the ability to edit or unpublish catalog content.
/// </summary>
public static class TenantCatalogProductEndpoints
{
    public static void MapTenantCatalogProductEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/tenant/catalog/products")
            .RequireAuthorization("TenantUserOnly")
            .RequireFeature(CatalogFeature.Key)
            .RequireRateLimiting("platform")
            .WithTags("Tenant Catalog Products");

        group.MapGet("/", async (
            int? page,
            int? pageSize,
            Guid? categoryId,
            string? status,
            bool? isVisible,
            bool? isAvailable,
            string? search,
            ITenantContext tenantContext,
            IProductManagementService service,
            CancellationToken cancellationToken) =>
        {
            var tenantId = TenantStaffEndpoints.RequireTenant(tenantContext);

            var filter = new ProductListFilter(categoryId, CatalogEndpointSupport.ParseStatus(status), isVisible, isAvailable, search);
            var result = await service.ListAsync(tenantId, page ?? 1, pageSize ?? 20, filter, cancellationToken);
            var items = result.Items.Select(CatalogEndpointSupport.ToResponse).ToArray();
            return Results.Ok(PagedResponse<ProductResponse>.Create(items, result.Page, result.PageSize, result.TotalCount));
        }).RequireAuthorization(p => p.RequirePermission(CatalogPermissions.ProductsView));

        group.MapPost("/", async (
            CreateProductRequest request,
            ITenantContext tenantContext,
            IProductManagementService service,
            CancellationToken cancellationToken) =>
        {
            var tenantId = TenantStaffEndpoints.RequireTenant(tenantContext);

            var product = await service.CreateAsync(tenantId, CatalogEndpointSupport.ToInput(request), cancellationToken);
            return Results.Created($"/api/tenant/catalog/products/{product.Id}", CatalogEndpointSupport.ToResponse(product));
        }).RequireAuthorization(p => p.RequirePermission(CatalogPermissions.ProductsCreate));

        group.MapGet("/{id:guid}", async (
            Guid id,
            ITenantContext tenantContext,
            IProductManagementService service,
            CancellationToken cancellationToken) =>
        {
            var tenantId = TenantStaffEndpoints.RequireTenant(tenantContext);

            var product = await service.GetAsync(tenantId, id, cancellationToken);
            return product is null ? Results.NotFound() : Results.Ok(CatalogEndpointSupport.ToResponse(product));
        }).RequireAuthorization(p => p.RequirePermission(CatalogPermissions.ProductsView));

        group.MapPut("/{id:guid}", async (
            Guid id,
            UpdateProductRequest request,
            ITenantContext tenantContext,
            IProductManagementService service,
            CancellationToken cancellationToken) =>
        {
            var tenantId = TenantStaffEndpoints.RequireTenant(tenantContext);

            var product = await service.UpdateAsync(tenantId, id, CatalogEndpointSupport.ToInput(request), cancellationToken);
            return Results.Ok(CatalogEndpointSupport.ToResponse(product));
        }).RequireAuthorization(p => p.RequirePermission(CatalogPermissions.ProductsUpdate));

        group.MapPut("/{id:guid}/category", async (
            Guid id,
            ChangeProductCategoryRequest request,
            ITenantContext tenantContext,
            IProductManagementService service,
            CancellationToken cancellationToken) =>
        {
            var tenantId = TenantStaffEndpoints.RequireTenant(tenantContext);

            var product = await service.ChangeCategoryAsync(tenantId, id, request.CategoryId, cancellationToken);
            return Results.Ok(CatalogEndpointSupport.ToResponse(product));
        }).RequireAuthorization(p => p.RequirePermission(CatalogPermissions.ProductsUpdate));

        group.MapPost("/{id:guid}/activate", async (
            Guid id,
            ITenantContext tenantContext,
            IProductManagementService service,
            CancellationToken cancellationToken) =>
        {
            var tenantId = TenantStaffEndpoints.RequireTenant(tenantContext);

            var product = await service.ActivateAsync(tenantId, id, cancellationToken);
            return Results.Ok(CatalogEndpointSupport.ToResponse(product));
        }).RequireAuthorization(p => p.RequirePermission(CatalogPermissions.ProductsUpdate));

        group.MapPut("/{id:guid}/visibility", async (
            Guid id,
            SetVisibilityRequest request,
            ITenantContext tenantContext,
            IProductManagementService service,
            CancellationToken cancellationToken) =>
        {
            var tenantId = TenantStaffEndpoints.RequireTenant(tenantContext);

            var product = await service.SetVisibilityAsync(tenantId, id, request.IsVisible, cancellationToken);
            return Results.Ok(CatalogEndpointSupport.ToResponse(product));
        }).RequireAuthorization(p => p.RequirePermission(CatalogPermissions.ProductsUpdate));

        group.MapPut("/{id:guid}/availability", async (
            Guid id,
            SetAvailabilityRequest request,
            ITenantContext tenantContext,
            IProductManagementService service,
            CancellationToken cancellationToken) =>
        {
            var tenantId = TenantStaffEndpoints.RequireTenant(tenantContext);

            var product = await service.SetAvailabilityAsync(tenantId, id, request.IsAvailable, cancellationToken);
            return Results.Ok(CatalogEndpointSupport.ToResponse(product));
        }).RequireAuthorization(p => p.RequirePermission(CatalogPermissions.AvailabilityManage));

        // Products are never physically removed; deleting archives the product and
        // returns the resulting state so the caller can see the new status.
        group.MapDelete("/{id:guid}", async (
            Guid id,
            ITenantContext tenantContext,
            IProductManagementService service,
            CancellationToken cancellationToken) =>
        {
            var tenantId = TenantStaffEndpoints.RequireTenant(tenantContext);

            var product = await service.ArchiveAsync(tenantId, id, cancellationToken);
            return Results.Ok(CatalogEndpointSupport.ToResponse(product));
        }).RequireAuthorization(p => p.RequirePermission(CatalogPermissions.ProductsDelete));

        group.MapGet("/{productId:guid}/modifier-groups", async (
            Guid productId,
            ITenantContext tenantContext,
            IProductModifierAssignmentService service,
            IModifierGroupManagementService modifierGroupService,
            CancellationToken cancellationToken) =>
        {
            var tenantId = TenantStaffEndpoints.RequireTenant(tenantContext);

            var assignments = await service.ListAsync(tenantId, productId, cancellationToken);
            return Results.Ok(await CatalogEndpointSupport.ToAssignmentResponsesAsync(assignments, modifierGroupService, tenantId, cancellationToken));
        }).RequireAuthorization(p => p.RequirePermission(CatalogPermissions.ModifiersManage));

        group.MapPut("/{productId:guid}/modifier-groups", async (
            Guid productId,
            ReplaceProductModifierGroupsRequest request,
            ITenantContext tenantContext,
            IProductModifierAssignmentService service,
            IModifierGroupManagementService modifierGroupService,
            CancellationToken cancellationToken) =>
        {
            var tenantId = TenantStaffEndpoints.RequireTenant(tenantContext);

            await service.ReplaceAsync(tenantId, productId, CatalogEndpointSupport.ToAssignments(request), cancellationToken);
            var assignments = await service.ListAsync(tenantId, productId, cancellationToken);
            return Results.Ok(await CatalogEndpointSupport.ToAssignmentResponsesAsync(assignments, modifierGroupService, tenantId, cancellationToken));
        }).RequireAuthorization(p => p.RequirePermission(CatalogPermissions.ModifiersManage));
    }
}
