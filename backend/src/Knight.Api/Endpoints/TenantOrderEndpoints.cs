using Ordering;
using Ordering.Domain;
using Knight.Api.Authorization;
using Knight.Application.Abstractions.Tenancy;
using Knight.Contracts.Common;
using Knight.Contracts.Ordering;

namespace Knight.Api.Endpoints;

public static class TenantOrderEndpoints
{
    public static void MapTenantOrderEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/tenant/orders")
            .RequireAuthorization("TenantUserOnly")
            .RequireFeature(OrderingFeature.Key)
            .RequireRateLimiting("platform")
            .WithTags("Tenant Orders");

        group.MapGet("/", async (
            int? page,
            int? pageSize,
            string? status,
            long? orderNumber,
            DateTimeOffset? createdFrom,
            DateTimeOffset? createdTo,
            ITenantContext tenantContext,
            IOrderManagementService service,
            CancellationToken cancellationToken) =>
        {
            var tenantId = TenantStaffEndpoints.RequireTenant(tenantContext);

            OrderStatus? parsedStatus = !string.IsNullOrWhiteSpace(status)
                ? OrderingEndpointSupport.ParseStatus(status)
                : null;

            var filter = new OrderListFilter(parsedStatus, orderNumber, createdFrom, createdTo);
            var result = await service.ListAsync(tenantId, page ?? 1, pageSize ?? 20, filter, cancellationToken);
            var items = result.Items.Select(OrderingEndpointSupport.ToSummaryResponse).ToArray();

            return Results.Ok(PagedResponse<OrderSummaryResponse>.Create(items, result.Page, result.PageSize, result.TotalCount));
        }).RequireAuthorization(p => p.RequirePermission(OrderingPermissions.OrdersView));

        group.MapGet("/{orderId:guid}", async (
            Guid orderId,
            ITenantContext tenantContext,
            IOrderManagementService service,
            CancellationToken cancellationToken) =>
        {
            var tenantId = TenantStaffEndpoints.RequireTenant(tenantContext);

            var order = await service.GetByIdAsync(tenantId, orderId, cancellationToken);
            if (order is null)
            {
                return Results.NotFound();
            }

            return Results.Ok(OrderingEndpointSupport.ToDetailResponse(order));
        }).RequireAuthorization(p => p.RequirePermission(OrderingPermissions.OrdersView));

        group.MapPost("/{orderId:guid}/status", async (
            Guid orderId,
            TransitionOrderStatusRequest request,
            ITenantContext tenantContext,
            IOrderManagementService service,
            CancellationToken cancellationToken) =>
        {
            var tenantId = TenantStaffEndpoints.RequireTenant(tenantContext);
            var targetStatus = OrderingEndpointSupport.ParseStatus(request.TargetStatus);

            var order = await service.TransitionStatusAsync(tenantId, orderId, targetStatus, request.Reason, cancellationToken);
            return Results.Ok(OrderingEndpointSupport.ToDetailResponse(order));
        }).RequireAuthorization(p => p.RequirePermission(OrderingPermissions.OrdersStatusUpdate));

        group.MapPost("/{orderId:guid}/cancel", async (
            Guid orderId,
            CancelOrderRequest? request,
            ITenantContext tenantContext,
            IOrderManagementService service,
            CancellationToken cancellationToken) =>
        {
            var tenantId = TenantStaffEndpoints.RequireTenant(tenantContext);

            var order = await service.CancelAsync(tenantId, orderId, request?.Reason, cancellationToken);
            return Results.Ok(OrderingEndpointSupport.ToDetailResponse(order));
        }).RequireAuthorization(p => p.RequirePermission(OrderingPermissions.OrdersCancel));
    }
}
