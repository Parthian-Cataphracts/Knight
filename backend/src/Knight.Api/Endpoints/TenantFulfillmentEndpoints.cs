using Fulfillment;
using Microsoft.AspNetCore.Mvc;
using Knight.Api.Authorization;
using Knight.Application.Abstractions.Tenancy;
using Knight.Contracts.Fulfillment;

namespace Knight.Api.Endpoints;

internal static class TenantFulfillmentEndpoints
{
    internal static void MapTenantFulfillmentEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/tenant/fulfillment/settings")
            .RequireAuthorization("TenantUserOnly")
            .RequireRateLimiting("platform")
            .WithTags("Tenant Fulfillment");

        group.MapGet("/", async (
            ITenantContext tenantContext,
            IFulfillmentManagementService service,
            CancellationToken cancellationToken) =>
        {
            var tenantId = TenantStaffEndpoints.RequireTenant(tenantContext);
            var settings = await service.GetSettingsAsync(tenantId, cancellationToken);
            return Results.Ok(DeliveryEndpointSupport.ToResponse(settings));
        })
        .RequireAuthorization(p => p.RequirePermission(FulfillmentPermissions.SettingsView))
        .WithName("GetTenantFulfillmentSettings");

        group.MapPut("/", async (
            ITenantContext tenantContext,
            [FromBody] UpdateTenantFulfillmentSettingsRequest request,
            IFulfillmentManagementService service,
            CancellationToken cancellationToken) =>
        {
            var tenantId = TenantStaffEndpoints.RequireTenant(tenantContext);
            var settings = await service.UpdateSettingsAsync(tenantId, request.PickupEnabled, cancellationToken);
            return Results.Ok(DeliveryEndpointSupport.ToResponse(settings));
        })
        .RequireAuthorization(p => p.RequirePermission(FulfillmentPermissions.SettingsUpdate))
        .WithName("UpdateTenantFulfillmentSettings");
    }
}
