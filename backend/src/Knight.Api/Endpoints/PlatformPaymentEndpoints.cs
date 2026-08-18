using Microsoft.AspNetCore.Mvc;
using Payment;
using Payment.Domain;

namespace Knight.Api.Endpoints;

internal static class PlatformPaymentEndpoints
{
    internal static void MapPlatformPaymentEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/platform/tenants/{tenantId:guid}/payments")
            .RequireAuthorization("PlatformAdminOnly")
            .RequireRateLimiting("platform")
            .WithTags("Platform Payments");

        group.MapGet("/", async (
            Guid tenantId,
            [FromQuery] string? status,
            [FromQuery] string? method,
            [FromQuery] Guid? orderId,
            [FromQuery] DateTimeOffset? fromDate,
            [FromQuery] DateTimeOffset? toDate,
            [FromQuery] int? page,
            [FromQuery] int? pageSize,
            IPaymentManagementService service,
            CancellationToken cancellationToken) =>
        {
            PaymentStatus? parsedStatus = null;
            if (!string.IsNullOrWhiteSpace(status) && Enum.TryParse<PaymentStatus>(status, ignoreCase: true, out var s))
            {
                parsedStatus = s;
            }

            PaymentMethod? parsedMethod = null;
            if (!string.IsNullOrWhiteSpace(method) && Enum.TryParse<PaymentMethod>(method, ignoreCase: true, out var m))
            {
                parsedMethod = m;
            }

            var filter = new PaymentListFilter(
                Status: parsedStatus,
                Method: parsedMethod,
                OrderId: orderId,
                FromDate: fromDate,
                ToDate: toDate,
                Page: page is > 0 ? page.Value : 1,
                PageSize: pageSize is > 0 and <= 100 ? pageSize.Value : 20);

            var response = await service.ListPaymentsAsync(tenantId, filter, cancellationToken);
            return Results.Ok(response);
        })
        .WithName("PlatformListTenantPayments");

        group.MapGet("/{id:guid}", async (
            Guid tenantId,
            Guid id,
            IPaymentManagementService service,
            CancellationToken cancellationToken) =>
        {
            var response = await service.GetPaymentByIdAsync(tenantId, id, cancellationToken);
            return Results.Ok(response);
        })
        .WithName("PlatformGetTenantPaymentById");
    }
}
