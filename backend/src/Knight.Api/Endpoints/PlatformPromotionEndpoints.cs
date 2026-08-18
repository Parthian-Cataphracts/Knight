using Microsoft.AspNetCore.Mvc;
using Knight.Contracts.Promotions;
using Promotions;
using Promotions.Domain;

namespace Knight.Api.Endpoints;

internal static class PlatformPromotionEndpoints
{
    internal static void MapPlatformPromotionEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/platform/tenants/{tenantId:guid}/promotions")
            .RequireAuthorization("PlatformAdminOnly")
            .RequireRateLimiting("platform")
            .WithTags("Platform Promotions");

        group.MapGet("/", async (
            Guid tenantId,
            [FromQuery] string? status,
            [FromQuery] string? discountType,
            [FromQuery] bool? requiresCoupon,
            [FromQuery] int? page,
            [FromQuery] int? pageSize,
            IPromotionManagementService service,
            CancellationToken cancellationToken) =>
        {
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
        .WithName("PlatformListTenantPromotions");

        group.MapGet("/{id:guid}", async (
            Guid tenantId,
            Guid id,
            IPromotionManagementService service,
            CancellationToken cancellationToken) =>
        {
            var response = await service.GetPromotionByIdAsync(tenantId, id, cancellationToken);
            return Results.Ok(response);
        })
        .WithName("PlatformGetTenantPromotionById");
    }
}
