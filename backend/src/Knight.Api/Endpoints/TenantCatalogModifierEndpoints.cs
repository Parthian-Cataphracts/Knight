using Catalog;
using Knight.Api.Authorization;
using Knight.Application.Abstractions.Tenancy;
using Knight.Contracts.Catalog;
using Knight.Contracts.Common;

namespace Knight.Api.Endpoints;

/// <summary>
/// Tenant self-administration of modifier groups and their modifiers.
///
/// The permission model declares a single <c>catalog.modifiers.manage</c>
/// permission for this area with no finer-grained view counterpart, so reads and
/// writes are guarded by the same key rather than by inventing an undeclared
/// permission. Modifier availability also stays under this key: it is part of
/// configuring a modifier group, not the top-level product availability control
/// that <c>catalog.availability.manage</c> exists for.
/// </summary>
public static class TenantCatalogModifierEndpoints
{
    public static void MapTenantCatalogModifierEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/tenant/catalog/modifier-groups")
            .RequireAuthorization("TenantUserOnly")
            .RequireFeature(CatalogFeature.Key)
            .RequireRateLimiting("platform")
            .WithTags("Tenant Catalog Modifiers");

        group.MapGet("/", async (
            int? page,
            int? pageSize,
            string? search,
            ITenantContext tenantContext,
            IModifierGroupManagementService service,
            CancellationToken cancellationToken) =>
        {
            var tenantId = TenantStaffEndpoints.RequireTenant(tenantContext);

            var result = await service.ListAsync(tenantId, page ?? 1, pageSize ?? 20, search, cancellationToken);
            var items = result.Items.Select(CatalogEndpointSupport.ToResponse).ToArray();
            return Results.Ok(PagedResponse<ModifierGroupResponse>.Create(items, result.Page, result.PageSize, result.TotalCount));
        }).RequireAuthorization(p => p.RequirePermission(CatalogPermissions.ModifiersManage));

        group.MapPost("/", async (
            CreateModifierGroupRequest request,
            ITenantContext tenantContext,
            IModifierGroupManagementService service,
            CancellationToken cancellationToken) =>
        {
            var tenantId = TenantStaffEndpoints.RequireTenant(tenantContext);

            var modifierGroup = await service.CreateAsync(tenantId, CatalogEndpointSupport.ToInput(request), cancellationToken);
            return Results.Created(
                $"/api/tenant/catalog/modifier-groups/{modifierGroup.Id}",
                CatalogEndpointSupport.ToResponse(modifierGroup));
        }).RequireAuthorization(p => p.RequirePermission(CatalogPermissions.ModifiersManage));

        group.MapGet("/{id:guid}", async (
            Guid id,
            ITenantContext tenantContext,
            IModifierGroupManagementService service,
            CancellationToken cancellationToken) =>
        {
            var tenantId = TenantStaffEndpoints.RequireTenant(tenantContext);

            var modifierGroup = await service.GetAsync(tenantId, id, cancellationToken);
            return modifierGroup is null ? Results.NotFound() : Results.Ok(CatalogEndpointSupport.ToResponse(modifierGroup));
        }).RequireAuthorization(p => p.RequirePermission(CatalogPermissions.ModifiersManage));

        group.MapPut("/{id:guid}", async (
            Guid id,
            UpdateModifierGroupRequest request,
            ITenantContext tenantContext,
            IModifierGroupManagementService service,
            CancellationToken cancellationToken) =>
        {
            var tenantId = TenantStaffEndpoints.RequireTenant(tenantContext);

            var modifierGroup = await service.UpdateAsync(tenantId, id, CatalogEndpointSupport.ToInput(request), cancellationToken);
            return Results.Ok(CatalogEndpointSupport.ToResponse(modifierGroup));
        }).RequireAuthorization(p => p.RequirePermission(CatalogPermissions.ModifiersManage));

        group.MapDelete("/{id:guid}", async (
            Guid id,
            ITenantContext tenantContext,
            IModifierGroupManagementService service,
            CancellationToken cancellationToken) =>
        {
            var tenantId = TenantStaffEndpoints.RequireTenant(tenantContext);

            await service.DeleteAsync(tenantId, id, cancellationToken);
            return Results.NoContent();
        }).RequireAuthorization(p => p.RequirePermission(CatalogPermissions.ModifiersManage));

        var modifiers = endpoints.MapGroup("/api/tenant/catalog/modifier-groups/{groupId:guid}/modifiers")
            .RequireAuthorization("TenantUserOnly")
            .RequireFeature(CatalogFeature.Key)
            .RequireRateLimiting("platform")
            .WithTags("Tenant Catalog Modifiers");

        modifiers.MapGet("/", async (
            Guid groupId,
            ITenantContext tenantContext,
            IModifierGroupManagementService service,
            CancellationToken cancellationToken) =>
        {
            var tenantId = TenantStaffEndpoints.RequireTenant(tenantContext);

            var items = await service.ListModifiersAsync(tenantId, groupId, cancellationToken);
            return Results.Ok(items.Select(CatalogEndpointSupport.ToResponse).ToArray());
        }).RequireAuthorization(p => p.RequirePermission(CatalogPermissions.ModifiersManage));

        modifiers.MapPost("/", async (
            Guid groupId,
            CreateModifierRequest request,
            ITenantContext tenantContext,
            IModifierGroupManagementService service,
            CancellationToken cancellationToken) =>
        {
            var tenantId = TenantStaffEndpoints.RequireTenant(tenantContext);

            var modifier = await service.CreateModifierAsync(tenantId, groupId, CatalogEndpointSupport.ToInput(request), cancellationToken);
            return Results.Created(
                $"/api/tenant/catalog/modifier-groups/{groupId}/modifiers/{modifier.Id}",
                CatalogEndpointSupport.ToResponse(modifier));
        }).RequireAuthorization(p => p.RequirePermission(CatalogPermissions.ModifiersManage));

        modifiers.MapGet("/{modifierId:guid}", async (
            Guid groupId,
            Guid modifierId,
            ITenantContext tenantContext,
            IModifierGroupManagementService service,
            CancellationToken cancellationToken) =>
        {
            var tenantId = TenantStaffEndpoints.RequireTenant(tenantContext);

            var modifier = await service.GetModifierAsync(tenantId, groupId, modifierId, cancellationToken);
            return modifier is null ? Results.NotFound() : Results.Ok(CatalogEndpointSupport.ToResponse(modifier));
        }).RequireAuthorization(p => p.RequirePermission(CatalogPermissions.ModifiersManage));

        modifiers.MapPut("/{modifierId:guid}", async (
            Guid groupId,
            Guid modifierId,
            UpdateModifierRequest request,
            ITenantContext tenantContext,
            IModifierGroupManagementService service,
            CancellationToken cancellationToken) =>
        {
            var tenantId = TenantStaffEndpoints.RequireTenant(tenantContext);

            var modifier = await service.UpdateModifierAsync(tenantId, groupId, modifierId, CatalogEndpointSupport.ToInput(request), cancellationToken);
            return Results.Ok(CatalogEndpointSupport.ToResponse(modifier));
        }).RequireAuthorization(p => p.RequirePermission(CatalogPermissions.ModifiersManage));

        modifiers.MapPut("/{modifierId:guid}/availability", async (
            Guid groupId,
            Guid modifierId,
            SetAvailabilityRequest request,
            ITenantContext tenantContext,
            IModifierGroupManagementService service,
            CancellationToken cancellationToken) =>
        {
            var tenantId = TenantStaffEndpoints.RequireTenant(tenantContext);

            var modifier = await service.SetModifierAvailabilityAsync(tenantId, groupId, modifierId, request.IsAvailable, cancellationToken);
            return Results.Ok(CatalogEndpointSupport.ToResponse(modifier));
        }).RequireAuthorization(p => p.RequirePermission(CatalogPermissions.ModifiersManage));
    }
}
