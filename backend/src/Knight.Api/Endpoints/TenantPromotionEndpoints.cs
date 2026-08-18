using Microsoft.AspNetCore.Mvc;
using Knight.Api.Authorization;
using Knight.Application.Abstractions.Tenancy;
using Knight.Contracts.Promotions;
using Promotions;
using Promotions.Domain;

namespace Knight.Api.Endpoints;

internal static class TenantPromotionEndpoints
{
    internal static void MapTenantPromotionEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/tenant/promotions")
            .RequireAuthorization("TenantUserOnly")
            .RequireFeature(PromotionsFeature.Key)
            .RequireRateLimiting("platform")
            .WithTags("Tenant Promotions");

        group.MapGet("/", async (
            [FromQuery] string? status,
            [FromQuery] string? discountType,
            [FromQuery] bool? requiresCoupon,
            [FromQuery] int? page,
            [FromQuery] int? pageSize,
            ITenantContext tenantContext,
            IPromotionManagementService service,
            CancellationToken cancellationToken) =>
        {
            var tenantId = TenantStaffEndpoints.RequireTenant(tenantContext);

            PromotionStatus? parsedStatus = null;
            if (!string.IsNullOrWhiteSpace(status) && Enum.TryParse<PromotionStatus>(status, ignoreCase: true, out var s))
            {
                parsedStatus = s;
            }

            PromotionDiscountType? parsedType = null;
            if (!string.IsNullOrWhiteSpace(discountType) && Enum.TryParse<PromotionDiscountType>(discountType, ignoreCase: true, out var dt))
            {
                parsedType = dt;
            }

            var filter = new PromotionListFilter(
                Status: parsedStatus,
                DiscountType: parsedType,
                RequiresCoupon: requiresCoupon,
                Page: page is > 0 ? page.Value : 1,
                PageSize: pageSize is > 0 and <= 100 ? pageSize.Value : 20);

            var (items, totalCount) = await service.ListPromotionsAsync(tenantId, filter, cancellationToken);
            return Results.Ok(new { items, totalCount });
        })
        .RequireAuthorization(p => p.RequirePermission(PromotionsPermissions.PromotionsView))
        .WithName("ListTenantPromotions");

        group.MapPost("/", async (
            [FromBody] CreatePromotionRequest request,
            ITenantContext tenantContext,
            IPromotionManagementService service,
            CancellationToken cancellationToken) =>
        {
            var tenantId = TenantStaffEndpoints.RequireTenant(tenantContext);
            var response = await service.CreatePromotionAsync(tenantId, request, cancellationToken);
            return Results.Created($"/api/tenant/promotions/{response.Id}", response);
        })
        .RequireAuthorization(p => p.RequirePermission(PromotionsPermissions.PromotionsCreate))
        .WithName("CreateTenantPromotion");

        group.MapGet("/{id:guid}", async (
            Guid id,
            ITenantContext tenantContext,
            IPromotionManagementService service,
            CancellationToken cancellationToken) =>
        {
            var tenantId = TenantStaffEndpoints.RequireTenant(tenantContext);
            var response = await service.GetPromotionByIdAsync(tenantId, id, cancellationToken);
            return Results.Ok(response);
        })
        .RequireAuthorization(p => p.RequirePermission(PromotionsPermissions.PromotionsView))
        .WithName("GetTenantPromotionById");

        group.MapPut("/{id:guid}", async (
            Guid id,
            [FromBody] UpdatePromotionRequest request,
            ITenantContext tenantContext,
            IPromotionManagementService service,
            CancellationToken cancellationToken) =>
        {
            var tenantId = TenantStaffEndpoints.RequireTenant(tenantContext);
            var response = await service.UpdatePromotionAsync(tenantId, id, request, cancellationToken);
            return Results.Ok(response);
        })
        .RequireAuthorization(p => p.RequirePermission(PromotionsPermissions.PromotionsUpdate))
        .WithName("UpdateTenantPromotion");

        group.MapPost("/{id:guid}/activate", async (
            Guid id,
            ITenantContext tenantContext,
            IPromotionManagementService service,
            CancellationToken cancellationToken) =>
        {
            var tenantId = TenantStaffEndpoints.RequireTenant(tenantContext);
            var response = await service.ActivatePromotionAsync(tenantId, id, cancellationToken);
            return Results.Ok(response);
        })
        .RequireAuthorization(p => p.RequirePermission(PromotionsPermissions.PromotionsUpdate))
        .WithName("ActivateTenantPromotion");

        group.MapPost("/{id:guid}/archive", async (
            Guid id,
            ITenantContext tenantContext,
            IPromotionManagementService service,
            CancellationToken cancellationToken) =>
        {
            var tenantId = TenantStaffEndpoints.RequireTenant(tenantContext);
            var response = await service.ArchivePromotionAsync(tenantId, id, cancellationToken);
            return Results.Ok(response);
        })
        .RequireAuthorization(p => p.RequirePermission(PromotionsPermissions.PromotionsArchive))
        .WithName("ArchiveTenantPromotion");
    }
}
