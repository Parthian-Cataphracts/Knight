using Catalog;
using Knight.Api.Authorization;
using Knight.Application.Abstractions.Tenancy;
using Knight.Contracts.Catalog;

namespace Knight.Api.Endpoints;

/// <summary>
/// Tenant self-administration of product media references. The API records
/// storage keys only — uploading bytes is a separate concern outside the catalog.
/// </summary>
public static class TenantCatalogMediaEndpoints
{
    public static void MapTenantCatalogMediaEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/tenant/catalog/products/{productId:guid}/media")
            .RequireAuthorization("TenantUserOnly")
            .RequireFeature(CatalogFeature.Key)
            .RequireRateLimiting("platform")
            .WithTags("Tenant Catalog Media");

        group.MapGet("/", async (
            Guid productId,
            ITenantContext tenantContext,
            IProductMediaManagementService service,
            CancellationToken cancellationToken) =>
        {
            var tenantId = TenantStaffEndpoints.RequireTenant(tenantContext);

            var media = await service.ListAsync(tenantId, productId, cancellationToken);
            return Results.Ok(media.Select(CatalogEndpointSupport.ToResponse).ToArray());
        }).RequireAuthorization(p => p.RequirePermission(CatalogPermissions.MediaManage));

        group.MapPost("/", async (
            Guid productId,
            AddProductMediaRequest request,
            ITenantContext tenantContext,
            IProductMediaManagementService service,
            CancellationToken cancellationToken) =>
        {
            var tenantId = TenantStaffEndpoints.RequireTenant(tenantContext);

            var media = await service.AddAsync(tenantId, productId, CatalogEndpointSupport.ToInput(request), cancellationToken);
            return Results.Created(
                $"/api/tenant/catalog/products/{productId}/media/{media.Id}",
                CatalogEndpointSupport.ToResponse(media));
        }).RequireAuthorization(p => p.RequirePermission(CatalogPermissions.MediaManage));

        group.MapPost("/{mediaId:guid}/primary", async (
            Guid productId,
            Guid mediaId,
            ITenantContext tenantContext,
            IProductMediaManagementService service,
            CancellationToken cancellationToken) =>
        {
            var tenantId = TenantStaffEndpoints.RequireTenant(tenantContext);

            await service.SetPrimaryAsync(tenantId, productId, mediaId, cancellationToken);
            return Results.NoContent();
        }).RequireAuthorization(p => p.RequirePermission(CatalogPermissions.MediaManage));

        group.MapDelete("/{mediaId:guid}", async (
            Guid productId,
            Guid mediaId,
            ITenantContext tenantContext,
            IProductMediaManagementService service,
            CancellationToken cancellationToken) =>
        {
            var tenantId = TenantStaffEndpoints.RequireTenant(tenantContext);

            await service.DeleteAsync(tenantId, productId, mediaId, cancellationToken);
            return Results.NoContent();
        }).RequireAuthorization(p => p.RequirePermission(CatalogPermissions.MediaManage));
    }
}
