using Ordering;
using Ordering.Domain;
using Knight.Contracts.Common;
using Knight.Contracts.Ordering;

namespace Knight.Api.Endpoints;

public static class PlatformOrderEndpoints
{
    public static void MapPlatformOrderEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/platform/tenants/{tenantId:guid}/orders")
            .RequireAuthorization("PlatformAdminOnly")
            .RequireRateLimiting("platform")
            .WithTags("Platform Tenant Orders");

        group.MapGet("/", async (
            Guid tenantId,
            int? page,
            int? pageSize,
            string? status,
            long? orderNumber,
            DateTimeOffset? createdFrom,
            DateTimeOffset? createdTo,
            IOrderManagementService service,
            CancellationToken cancellationToken) =>
        {
            OrderStatus? parsedStatus = !string.IsNullOrWhiteSpace(status)
                ? OrderingEndpointSupport.ParseStatus(status)
                : null;

            var filter = new OrderListFilter(parsedStatus, orderNumber, createdFrom, createdTo);
            var result = await service.ListAsync(tenantId, page ?? 1, pageSize ?? 20, filter, cancellationToken);
            var items = result.Items.Select(OrderingEndpointSupport.ToSummaryResponse).ToArray();

            return Results.Ok(PagedResponse<OrderSummaryResponse>.Create(items, result.Page, result.PageSize, result.TotalCount));
        });

        group.MapGet("/{orderId:guid}", async (
            Guid tenantId,
            Guid orderId,
            IOrderManagementService service,
            CancellationToken cancellationToken) =>
        {
            var order = await service.GetByIdAsync(tenantId, orderId, cancellationToken);
            if (order is null)
            {
                return Results.NotFound();
            }

            return Results.Ok(OrderingEndpointSupport.ToDetailResponse(order));
        });

        group.MapPost("/{orderId:guid}/status", async (
            Guid tenantId,
            Guid orderId,
            TransitionOrderStatusRequest request,
            IOrderManagementService service,
            CancellationToken cancellationToken) =>
        {
            var targetStatus = OrderingEndpointSupport.ParseStatus(request.TargetStatus);

            var order = await service.TransitionStatusAsync(tenantId, orderId, targetStatus, request.Reason, cancellationToken);
            return Results.Ok(OrderingEndpointSupport.ToDetailResponse(order));
        });

        group.MapPost("/{orderId:guid}/cancel", async (
            Guid tenantId,
            Guid orderId,
            CancelOrderRequest? request,
            IOrderManagementService service,
            CancellationToken cancellationToken) =>
        {
            var order = await service.CancelAsync(tenantId, orderId, request?.Reason, cancellationToken);
            return Results.Ok(OrderingEndpointSupport.ToDetailResponse(order));
        });
    }
}
