using Catalog;
using Knight.Api.Authorization;
using Knight.Application.Abstractions.Tenancy;
using Knight.Contracts.Catalog;

namespace Knight.Api.Endpoints;

/// <summary>
/// Tenant self-administration of product variants. Reads reuse
/// <c>catalog.products.view</c> — listing a product's variants is part of viewing
/// the product — while every mutation requires <c>catalog.variants.manage</c>,
/// except the availability toggle which belongs to
/// <c>catalog.availability.manage</c> for the same reason as products.
/// </summary>
public static class TenantCatalogVariantEndpoints
{
    public static void MapTenantCatalogVariantEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/tenant/catalog/products/{productId:guid}/variants")
            .RequireAuthorization("TenantUserOnly")
            .RequireFeature(CatalogFeature.Key)
            .RequireRateLimiting("platform")
            .WithTags("Tenant Catalog Variants");

        group.MapGet("/", async (
            Guid productId,
            ITenantContext tenantContext,
            IProductVariantManagementService service,
            CancellationToken cancellationToken) =>
        {
            var tenantId = TenantStaffEndpoints.RequireTenant(tenantContext);

            var variants = await service.ListAsync(tenantId, productId, cancellationToken);
            return Results.Ok(variants.Select(CatalogEndpointSupport.ToResponse).ToArray());
        }).RequireAuthorization(p => p.RequirePermission(CatalogPermissions.ProductsView));

        group.MapPost("/", async (
            Guid productId,
            CreateProductVariantRequest request,
            ITenantContext tenantContext,
            IProductVariantManagementService service,
            CancellationToken cancellationToken) =>
        {
            var tenantId = TenantStaffEndpoints.RequireTenant(tenantContext);

            var variant = await service.CreateAsync(tenantId, productId, CatalogEndpointSupport.ToInput(request), cancellationToken);
            return Results.Created(
                $"/api/tenant/catalog/products/{productId}/variants/{variant.Id}",
                CatalogEndpointSupport.ToResponse(variant));
        }).RequireAuthorization(p => p.RequirePermission(CatalogPermissions.VariantsManage));

        group.MapGet("/{variantId:guid}", async (
            Guid productId,
            Guid variantId,
            ITenantContext tenantContext,
            IProductVariantManagementService service,
            CancellationToken cancellationToken) =>
        {
            var tenantId = TenantStaffEndpoints.RequireTenant(tenantContext);

            var variant = await service.GetAsync(tenantId, productId, variantId, cancellationToken);
            return variant is null ? Results.NotFound() : Results.Ok(CatalogEndpointSupport.ToResponse(variant));
        }).RequireAuthorization(p => p.RequirePermission(CatalogPermissions.ProductsView));

        group.MapPut("/{variantId:guid}", async (
            Guid productId,
            Guid variantId,
            UpdateProductVariantRequest request,
            ITenantContext tenantContext,
            IProductVariantManagementService service,
            CancellationToken cancellationToken) =>
        {
            var tenantId = TenantStaffEndpoints.RequireTenant(tenantContext);

            var variant = await service.UpdateAsync(tenantId, productId, variantId, CatalogEndpointSupport.ToInput(request), cancellationToken);
            return Results.Ok(CatalogEndpointSupport.ToResponse(variant));
        }).RequireAuthorization(p => p.RequirePermission(CatalogPermissions.VariantsManage));

        group.MapPost("/{variantId:guid}/default", async (
            Guid productId,
            Guid variantId,
            ITenantContext tenantContext,
            IProductVariantManagementService service,
            CancellationToken cancellationToken) =>
        {
            var tenantId = TenantStaffEndpoints.RequireTenant(tenantContext);

            await service.SetDefaultAsync(tenantId, productId, variantId, cancellationToken);
            return Results.NoContent();
        }).RequireAuthorization(p => p.RequirePermission(CatalogPermissions.VariantsManage));

        group.MapPut("/{variantId:guid}/availability", async (
            Guid productId,
            Guid variantId,
            SetAvailabilityRequest request,
            ITenantContext tenantContext,
            IProductVariantManagementService service,
            CancellationToken cancellationToken) =>
        {
            var tenantId = TenantStaffEndpoints.RequireTenant(tenantContext);

            var variant = await service.SetAvailabilityAsync(tenantId, productId, variantId, request.IsAvailable, cancellationToken);
            return Results.Ok(CatalogEndpointSupport.ToResponse(variant));
        }).RequireAuthorization(p => p.RequirePermission(CatalogPermissions.AvailabilityManage));

        // Variants are never physically removed; deleting deactivates the variant
        // so historical references can still resolve it.
        group.MapDelete("/{variantId:guid}", async (
            Guid productId,
            Guid variantId,
            ITenantContext tenantContext,
            IProductVariantManagementService service,
            CancellationToken cancellationToken) =>
        {
            var tenantId = TenantStaffEndpoints.RequireTenant(tenantContext);

            await service.DeactivateAsync(tenantId, productId, variantId, cancellationToken);
            return Results.NoContent();
        }).RequireAuthorization(p => p.RequirePermission(CatalogPermissions.VariantsManage));
    }
}
