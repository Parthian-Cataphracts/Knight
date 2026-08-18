using Microsoft.AspNetCore.Mvc;
using Knight.Api.Authorization;
using Knight.Application.Abstractions.Tenancy;
using Knight.Contracts.Promotions;
using Promotions;
using Promotions.Domain;

namespace Knight.Api.Endpoints;

internal static class TenantCouponEndpoints
{
    internal static void MapTenantCouponEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/tenant/coupons")
            .RequireAuthorization("TenantUserOnly")
            .RequireFeature(PromotionsFeature.Key)
            .RequireRateLimiting("platform")
            .WithTags("Tenant Coupons");

        group.MapGet("/", async (
            [FromQuery] Guid? promotionId,
            [FromQuery] string? status,
            [FromQuery] int? page,
            [FromQuery] int? pageSize,
            ITenantContext tenantContext,
            ICouponManagementService service,
            CancellationToken cancellationToken) =>
        {
            var tenantId = TenantStaffEndpoints.RequireTenant(tenantContext);

            CouponStatus? parsedStatus = null;
            if (!string.IsNullOrWhiteSpace(status) && Enum.TryParse<CouponStatus>(status, ignoreCase: true, out var s))
            {
                parsedStatus = s;
            }

            var filter = new CouponListFilter(
                PromotionId: promotionId,
                Status: parsedStatus,
                Page: page is > 0 ? page.Value : 1,
                PageSize: pageSize is > 0 and <= 100 ? pageSize.Value : 20);

            var (items, totalCount) = await service.ListCouponsAsync(tenantId, filter, cancellationToken);
            return Results.Ok(new { items, totalCount });
        })
        .RequireAuthorization(p => p.RequirePermission(PromotionsPermissions.CouponsView))
        .WithName("ListTenantCoupons");

        group.MapPost("/", async (
            [FromBody] CreateCouponRequest request,
            ITenantContext tenantContext,
            ICouponManagementService service,
            CancellationToken cancellationToken) =>
        {
            var tenantId = TenantStaffEndpoints.RequireTenant(tenantContext);
            var response = await service.CreateCouponAsync(tenantId, request, cancellationToken);
            return Results.Created($"/api/tenant/coupons/{response.Id}", response);
        })
        .RequireAuthorization(p => p.RequirePermission(PromotionsPermissions.CouponsCreate))
        .WithName("CreateTenantCoupon");

        group.MapGet("/{id:guid}", async (
            Guid id,
            ITenantContext tenantContext,
            ICouponManagementService service,
            CancellationToken cancellationToken) =>
        {
            var tenantId = TenantStaffEndpoints.RequireTenant(tenantContext);
            var response = await service.GetCouponByIdAsync(tenantId, id, cancellationToken);
            return Results.Ok(response);
        })
        .RequireAuthorization(p => p.RequirePermission(PromotionsPermissions.CouponsView))
        .WithName("GetTenantCouponById");

        group.MapPut("/{id:guid}", async (
            Guid id,
            [FromBody] UpdateCouponRequest request,
            ITenantContext tenantContext,
            ICouponManagementService service,
            CancellationToken cancellationToken) =>
        {
            var tenantId = TenantStaffEndpoints.RequireTenant(tenantContext);
            var response = await service.UpdateCouponAsync(tenantId, id, request, cancellationToken);
            return Results.Ok(response);
        })
        .RequireAuthorization(p => p.RequirePermission(PromotionsPermissions.CouponsUpdate))
        .WithName("UpdateTenantCoupon");

        group.MapPost("/{id:guid}/archive", async (
            Guid id,
            ITenantContext tenantContext,
            ICouponManagementService service,
            CancellationToken cancellationToken) =>
        {
            var tenantId = TenantStaffEndpoints.RequireTenant(tenantContext);
            var response = await service.ArchiveCouponAsync(tenantId, id, cancellationToken);
            return Results.Ok(response);
        })
        .RequireAuthorization(p => p.RequirePermission(PromotionsPermissions.CouponsArchive))
        .WithName("ArchiveTenantCoupon");
    }
}
