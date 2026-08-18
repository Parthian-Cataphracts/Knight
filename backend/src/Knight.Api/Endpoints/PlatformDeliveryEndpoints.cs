using Delivery;
using Delivery.Domain;
using Microsoft.AspNetCore.Mvc;
using Knight.Contracts.Common;
using Knight.Contracts.Delivery;

namespace Knight.Api.Endpoints;

internal static class PlatformDeliveryEndpoints
{
    internal static void MapPlatformDeliveryEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/platform/tenants/{tenantId:guid}/delivery")
            .RequireAuthorization("PlatformAdminOnly")
            .RequireRateLimiting("platform")
            .WithTags("Platform Delivery");

        // Settings
        group.MapGet("/settings", async (
            Guid tenantId,
            IDeliveryManagementService service,
            CancellationToken cancellationToken) =>
        {
            var settings = await service.GetSettingsAsync(tenantId, cancellationToken);
            return Results.Ok(DeliveryEndpointSupport.ToResponse(settings));
        })
        .WithName("PlatformGetTenantDeliverySettings");

        group.MapPut("/settings", async (
            Guid tenantId,
            [FromBody] UpdateTenantDeliverySettingsRequest request,
            IDeliveryManagementService service,
            CancellationToken cancellationToken) =>
        {
            var settings = await service.UpdateSettingsAsync(
                tenantId,
                request.IsAcceptingDeliveryOrders,
                request.DefaultMinimumOrderSubtotal,
                cancellationToken);
            return Results.Ok(DeliveryEndpointSupport.ToResponse(settings));
        })
        .WithName("PlatformUpdateTenantDeliverySettings");

        // Zones
        group.MapGet("/zones", async (
            Guid tenantId,
            int? page,
            int? pageSize,
            DeliveryZoneStatus? status,
            IDeliveryManagementService service,
            CancellationToken cancellationToken) =>
        {
            var p = page ?? 1;
            var ps = pageSize ?? 20;
            var (items, totalCount) = await service.ListZonesAsync(tenantId, p, ps, status, cancellationToken);
            var responses = items.Select(DeliveryEndpointSupport.ToResponse).ToArray();

            return Results.Ok(PagedResponse<DeliveryZoneResponse>.Create(responses, p, ps, totalCount));
        })
        .WithName("PlatformListTenantDeliveryZones");

        group.MapPost("/zones", async (
            Guid tenantId,
            [FromBody] CreateDeliveryZoneRequest request,
            IDeliveryManagementService service,
            CancellationToken cancellationToken) =>
        {
            var zone = await service.CreateZoneAsync(
                tenantId,
                request.Name,
                request.Fee,
                request.MinimumOrderSubtotal,
                request.DisplayOrder,
                cancellationToken);
            return Results.Created($"/api/platform/tenants/{tenantId}/delivery/zones/{zone.Id}", DeliveryEndpointSupport.ToResponse(zone));
        })
        .WithName("PlatformCreateTenantDeliveryZone");

        group.MapGet("/zones/{id:guid}", async (
            Guid tenantId,
            Guid id,
            IDeliveryManagementService service,
            CancellationToken cancellationToken) =>
        {
            var zone = await service.GetZoneByIdAsync(tenantId, id, cancellationToken);
            if (zone is null)
            {
                return Results.NotFound();
            }

            return Results.Ok(DeliveryEndpointSupport.ToResponse(zone));
        })
        .WithName("PlatformGetTenantDeliveryZone");

        group.MapPut("/zones/{id:guid}", async (
            Guid tenantId,
            Guid id,
            [FromBody] UpdateDeliveryZoneRequest request,
            IDeliveryManagementService service,
            CancellationToken cancellationToken) =>
        {
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
        .WithName("PlatformUpdateTenantDeliveryZone");

        group.MapPost("/zones/{id:guid}/archive", async (
            Guid tenantId,
            Guid id,
            IDeliveryManagementService service,
            CancellationToken cancellationToken) =>
        {
            var zone = await service.ArchiveZoneAsync(tenantId, id, cancellationToken);
            return Results.Ok(DeliveryEndpointSupport.ToResponse(zone));
        })
        .WithName("PlatformArchiveTenantDeliveryZone");

        group.MapPost("/zones/{id:guid}/restore", async (
            Guid tenantId,
            Guid id,
            IDeliveryManagementService service,
            CancellationToken cancellationToken) =>
        {
            var zone = await service.RestoreZoneAsync(tenantId, id, cancellationToken);
            return Results.Ok(DeliveryEndpointSupport.ToResponse(zone));
        })
        .WithName("PlatformRestoreTenantDeliveryZone");
    }
}
