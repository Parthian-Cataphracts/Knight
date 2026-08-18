using Fulfillment;
using Microsoft.AspNetCore.Mvc;
using Knight.Contracts.Fulfillment;

namespace Knight.Api.Endpoints;

internal static class PlatformFulfillmentEndpoints
{
    internal static void MapPlatformFulfillmentEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/platform/tenants/{tenantId:guid}/fulfillment/settings")
            .RequireAuthorization("PlatformAdminOnly")
            .RequireRateLimiting("platform")
            .WithTags("Platform Fulfillment");

        group.MapGet("/", async (
            Guid tenantId,
            IFulfillmentManagementService service,
            CancellationToken cancellationToken) =>
        {
            var settings = await service.GetSettingsAsync(tenantId, cancellationToken);
            return Results.Ok(DeliveryEndpointSupport.ToResponse(settings));
        })
        .WithName("PlatformGetTenantFulfillmentSettings");

        group.MapPut("/", async (
            Guid tenantId,
            [FromBody] UpdateTenantFulfillmentSettingsRequest request,
            IFulfillmentManagementService service,
            CancellationToken cancellationToken) =>
        {
            var settings = await service.UpdateSettingsAsync(tenantId, request.PickupEnabled, cancellationToken);
            return Results.Ok(DeliveryEndpointSupport.ToResponse(settings));
        })
        .WithName("PlatformUpdateTenantFulfillmentSettings");
    }
}
