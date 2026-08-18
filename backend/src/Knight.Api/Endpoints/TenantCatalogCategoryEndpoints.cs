using Catalog;
using Knight.Api.Authorization;
using Knight.Application.Abstractions.Tenancy;
using Knight.Contracts.Catalog;
using Knight.Contracts.Common;

namespace Knight.Api.Endpoints;

/// <summary>
/// Tenant self-administration of catalog categories. Always operates on
/// <see cref="ITenantContext.TenantId"/> — the tenant is never accepted from the
/// request. Every handler verifies the catalog feature for the current tenant
/// before touching an application service.
/// </summary>
public static class TenantCatalogCategoryEndpoints
{
    public static void MapTenantCatalogCategoryEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/tenant/catalog/categories")
            .RequireAuthorization("TenantUserOnly")
            .RequireFeature(CatalogFeature.Key)
            .RequireRateLimiting("platform")
            .WithTags("Tenant Catalog Categories");

        group.MapGet("/", async (
            int? page,
            int? pageSize,
            string? search,
            bool? isVisible,
            ITenantContext tenantContext,
            ICategoryManagementService service,
            CancellationToken cancellationToken) =>
        {
            var tenantId = TenantStaffEndpoints.RequireTenant(tenantContext);

            var result = await service.ListAsync(tenantId, page ?? 1, pageSize ?? 20, search, isVisible, cancellationToken);
            var items = result.Items.Select(CatalogEndpointSupport.ToResponse).ToArray();
            return Results.Ok(PagedResponse<CategoryResponse>.Create(items, result.Page, result.PageSize, result.TotalCount));
        }).RequireAuthorization(p => p.RequirePermission(CatalogPermissions.CategoriesView));

        group.MapPost("/", async (
            CreateCategoryRequest request,
            ITenantContext tenantContext,
            ICategoryManagementService service,
            CancellationToken cancellationToken) =>
        {
            var tenantId = TenantStaffEndpoints.RequireTenant(tenantContext);

            var category = await service.CreateAsync(tenantId, CatalogEndpointSupport.ToInput(request), cancellationToken);
            return Results.Created($"/api/tenant/catalog/categories/{category.Id}", CatalogEndpointSupport.ToResponse(category));
        }).RequireAuthorization(p => p.RequirePermission(CatalogPermissions.CategoriesCreate));

        group.MapGet("/{id:guid}", async (
            Guid id,
            ITenantContext tenantContext,
            ICategoryManagementService service,
            CancellationToken cancellationToken) =>
        {
            var tenantId = TenantStaffEndpoints.RequireTenant(tenantContext);

            // A category belonging to another tenant is indistinguishable from one
            // that does not exist — the lookup is tenant-scoped and both return 404.
            var category = await service.GetAsync(tenantId, id, cancellationToken);
            return category is null ? Results.NotFound() : Results.Ok(CatalogEndpointSupport.ToResponse(category));
        }).RequireAuthorization(p => p.RequirePermission(CatalogPermissions.CategoriesView));

        group.MapPut("/{id:guid}", async (
            Guid id,
            UpdateCategoryRequest request,
            ITenantContext tenantContext,
            ICategoryManagementService service,
            CancellationToken cancellationToken) =>
        {
            var tenantId = TenantStaffEndpoints.RequireTenant(tenantContext);

            var category = await service.UpdateAsync(tenantId, id, CatalogEndpointSupport.ToInput(request), cancellationToken);
            return Results.Ok(CatalogEndpointSupport.ToResponse(category));
        }).RequireAuthorization(p => p.RequirePermission(CatalogPermissions.CategoriesUpdate));

        group.MapPut("/{id:guid}/visibility", async (
            Guid id,
            SetVisibilityRequest request,
            ITenantContext tenantContext,
            ICategoryManagementService service,
            CancellationToken cancellationToken) =>
        {
            var tenantId = TenantStaffEndpoints.RequireTenant(tenantContext);

            var category = await service.SetVisibilityAsync(tenantId, id, request.IsVisible, cancellationToken);
            return Results.Ok(CatalogEndpointSupport.ToResponse(category));
        }).RequireAuthorization(p => p.RequirePermission(CatalogPermissions.CategoriesUpdate));

        group.MapDelete("/{id:guid}", async (
            Guid id,
            ITenantContext tenantContext,
            ICategoryManagementService service,
            CancellationToken cancellationToken) =>
        {
            var tenantId = TenantStaffEndpoints.RequireTenant(tenantContext);

            // A category still holding products raises a conflict in the application
            // service; the exception middleware translates that to HTTP 409.
            await service.DeleteAsync(tenantId, id, cancellationToken);
            return Results.NoContent();
        }).RequireAuthorization(p => p.RequirePermission(CatalogPermissions.CategoriesDelete));
    }
}
