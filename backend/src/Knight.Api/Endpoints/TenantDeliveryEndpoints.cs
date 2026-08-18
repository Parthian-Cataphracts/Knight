using Delivery;
using Delivery.Domain;
using Microsoft.AspNetCore.Mvc;
using Knight.Api.Authorization;
using Knight.Application.Abstractions.Tenancy;
using Knight.Contracts.Common;
using Knight.Contracts.Delivery;

namespace Knight.Api.Endpoints;

internal static class TenantDeliveryEndpoints
{
    internal static void MapTenantDeliveryEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/tenant/delivery")
            .RequireAuthorization("TenantUserOnly")
            .RequireFeature(DeliveryFeature.Key)
            .RequireRateLimiting("platform")
            .WithTags("Tenant Delivery");

        // Settings
        group.MapGet("/settings", async (
            ITenantContext tenantContext,
            IDeliveryManagementService service,
            CancellationToken cancellationToken) =>
        {
            var tenantId = TenantStaffEndpoints.RequireTenant(tenantContext);
            var settings = await service.GetSettingsAsync(tenantId, cancellationToken);
            return Results.Ok(DeliveryEndpointSupport.ToResponse(settings));
        })
        .RequireAuthorization(p => p.RequirePermission(DeliveryPermissions.SettingsView))
        .WithName("GetTenantDeliverySettings");

        group.MapPut("/settings", async (
            ITenantContext tenantContext,
            [FromBody] UpdateTenantDeliverySettingsRequest request,
            IDeliveryManagementService service,
            CancellationToken cancellationToken) =>
        {
            var tenantId = TenantStaffEndpoints.RequireTenant(tenantContext);
            var settings = await service.UpdateSettingsAsync(
                tenantId,
                request.IsAcceptingDeliveryOrders,
                request.DefaultMinimumOrderSubtotal,
                cancellationToken);
            return Results.Ok(DeliveryEndpointSupport.ToResponse(settings));
        })
        .RequireAuthorization(p => p.RequirePermission(DeliveryPermissions.SettingsUpdate))
        .WithName("UpdateTenantDeliverySettings");

        // Zones
        group.MapGet("/zones", async (
            int? page,
            int? pageSize,
            DeliveryZoneStatus? status,
            ITenantContext tenantContext,
            IDeliveryManagementService service,
            CancellationToken cancellationToken) =>
        {
            var tenantId = TenantStaffEndpoints.RequireTenant(tenantContext);
            var p = page ?? 1;
            var ps = pageSize ?? 20;

            var (items, totalCount) = await service.ListZonesAsync(tenantId, p, ps, status, cancellationToken);
            var responses = items.Select(DeliveryEndpointSupport.ToResponse).ToArray();

            return Results.Ok(PagedResponse<DeliveryZoneResponse>.Create(responses, p, ps, totalCount));
        })
        .RequireAuthorization(p => p.RequirePermission(DeliveryPermissions.ZonesView))
        .WithName("ListTenantDeliveryZones");

        group.MapPost("/zones", async (
            ITenantContext tenantContext,
            [FromBody] CreateDeliveryZoneRequest request,
            IDeliveryManagementService service,
            CancellationToken cancellationToken) =>
        {
            var tenantId = TenantStaffEndpoints.RequireTenant(tenantContext);
            var zone = await service.CreateZoneAsync(
                tenantId,
                request.Name,
                request.Fee,
                request.MinimumOrderSubtotal,
                request.DisplayOrder,
                cancellationToken);
            return Results.Created($"/api/tenant/delivery/zones/{zone.Id}", DeliveryEndpointSupport.ToResponse(zone));
        })
        .RequireAuthorization(p => p.RequirePermission(DeliveryPermissions.ZonesCreate))
        .WithName("CreateTenantDeliveryZone");

        group.MapGet("/zones/{id:guid}", async (
            Guid id,
            ITenantContext tenantContext,
            IDeliveryManagementService service,
            CancellationToken cancellationToken) =>
        {
            var tenantId = TenantStaffEndpoints.RequireTenant(tenantContext);
            var zone = await service.GetZoneByIdAsync(tenantId, id, cancellationToken);
            if (zone is null)
            {
                return Results.NotFound();
            }

            return Results.Ok(DeliveryEndpointSupport.ToResponse(zone));
        })
        .RequireAuthorization(p => p.RequirePermission(DeliveryPermissions.ZonesView))
        .WithName("GetTenantDeliveryZone");

        group.MapPut("/zones/{id:guid}", async (
            Guid id,
            ITenantContext tenantContext,
            [FromBody] UpdateDeliveryZoneRequest request,
            IDeliveryManagementService service,
            CancellationToken cancellationToken) =>
        {
            var tenantId = TenantStaffEndpoints.RequireTenant(tenantContext);
            var zone = await service.UpdateZoneAsync(
                tenantId,
                id,
                request.Name,
                request.Fee,
                request.MinimumOrderSubtotal,
                request.DisplayOrder,
                cancellationToken);
            return Results.Ok(DeliveryEndpointSupport.ToResponse(zone));
        })
        .RequireAuthorization(p => p.RequirePermission(DeliveryPermissions.ZonesUpdate))
        .WithName("UpdateTenantDeliveryZone");

        group.MapPost("/zones/{id:guid}/archive", async (
            Guid id,
            ITenantContext tenantContext,
            IDeliveryManagementService service,
            CancellationToken cancellationToken) =>
        {
            var tenantId = TenantStaffEndpoints.RequireTenant(tenantContext);
            var zone = await service.ArchiveZoneAsync(tenantId, id, cancellationToken);
            return Results.Ok(DeliveryEndpointSupport.ToResponse(zone));
        })
        .RequireAuthorization(p => p.RequirePermission(DeliveryPermissions.ZonesArchive))
        .WithName("ArchiveTenantDeliveryZone");

        group.MapPost("/zones/{id:guid}/restore", async (
            Guid id,
            ITenantContext tenantContext,
            IDeliveryManagementService service,
            CancellationToken cancellationToken) =>
        {
            var tenantId = TenantStaffEndpoints.RequireTenant(tenantContext);
            var zone = await service.RestoreZoneAsync(tenantId, id, cancellationToken);
            return Results.Ok(DeliveryEndpointSupport.ToResponse(zone));
        })
        .RequireAuthorization(p => p.RequirePermission(DeliveryPermissions.ZonesUpdate))
        .WithName("RestoreTenantDeliveryZone");
    }
}
