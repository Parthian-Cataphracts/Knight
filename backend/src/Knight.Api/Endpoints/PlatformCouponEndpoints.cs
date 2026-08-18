using Microsoft.AspNetCore.Mvc;
using Knight.Contracts.Promotions;
using Promotions;
using Promotions.Domain;

namespace Knight.Api.Endpoints;

internal static class PlatformCouponEndpoints
{
    internal static void MapPlatformCouponEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/platform/tenants/{tenantId:guid}/coupons")
            .RequireAuthorization("PlatformAdminOnly")
            .RequireRateLimiting("platform")
            .WithTags("Platform Coupons");

        group.MapGet("/", async (
            Guid tenantId,
            [FromQuery] Guid? promotionId,
            [FromQuery] string? status,
            [FromQuery] int? page,
            [FromQuery] int? pageSize,
            ICouponManagementService service,
            CancellationToken cancellationToken) =>
        {
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
        .WithName("PlatformListTenantCoupons");

        group.MapGet("/{id:guid}", async (
            Guid tenantId,
            Guid id,
            ICouponManagementService service,
            CancellationToken cancellationToken) =>
        {
            var response = await service.GetCouponByIdAsync(tenantId, id, cancellationToken);
            return Results.Ok(response);
        })
        .WithName("PlatformGetTenantCouponById");
    }
}
