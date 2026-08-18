using Microsoft.AspNetCore.Mvc;
using Payment;
using Payment.Domain;
using Knight.Api.Authorization;
using Knight.Application.Abstractions.Tenancy;
using Knight.Contracts.Payment;

namespace Knight.Api.Endpoints;

internal static class TenantPaymentEndpoints
{
    internal static void MapTenantPaymentEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/tenant/payments")
            .RequireAuthorization("TenantUserOnly")
            .RequireFeature(PaymentFeature.Key)
            .RequireRateLimiting("platform")
            .WithTags("Tenant Payments");

        group.MapGet("/", async (
            [FromQuery] string? status,
            [FromQuery] string? method,
            [FromQuery] Guid? orderId,
            [FromQuery] DateTimeOffset? fromDate,
            [FromQuery] DateTimeOffset? toDate,
            [FromQuery] int? page,
            [FromQuery] int? pageSize,
            ITenantContext tenantContext,
            IPaymentManagementService service,
            CancellationToken cancellationToken) =>
        {
            var tenantId = TenantStaffEndpoints.RequireTenant(tenantContext);

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
        .RequireAuthorization(p => p.RequirePermission(PaymentPermissions.View))
        .WithName("ListTenantPayments");

        group.MapGet("/{id:guid}", async (
            Guid id,
            ITenantContext tenantContext,
            IPaymentManagementService service,
            CancellationToken cancellationToken) =>
        {
            var tenantId = TenantStaffEndpoints.RequireTenant(tenantContext);
            var response = await service.GetPaymentByIdAsync(tenantId, id, cancellationToken);
            return Results.Ok(response);
        })
        .RequireAuthorization(p => p.RequirePermission(PaymentPermissions.View))
        .WithName("GetTenantPaymentById");

        group.MapPost("/{id:guid}/mark-paid", async (
            Guid id,
            [FromBody] MarkPaymentPaidRequest? request,
            ITenantContext tenantContext,
            IPaymentManagementService service,
            CancellationToken cancellationToken) =>
        {
            var tenantId = TenantStaffEndpoints.RequireTenant(tenantContext);
            var response = await service.MarkPaidAsync(tenantId, id, request ?? new MarkPaymentPaidRequest(), cancellationToken);
            return Results.Ok(response);
        })
        .RequireAuthorization(p => p.RequirePermission(PaymentPermissions.StatusManage))
        .WithName("MarkTenantPaymentPaid");

        group.MapPost("/{id:guid}/cancel", async (
            Guid id,
            [FromBody] CancelPaymentRequest? request,
            ITenantContext tenantContext,
            IPaymentManagementService service,
            CancellationToken cancellationToken) =>
        {
            var tenantId = TenantStaffEndpoints.RequireTenant(tenantContext);
            var response = await service.CancelPaymentAsync(tenantId, id, request ?? new CancelPaymentRequest(), cancellationToken);
            return Results.Ok(response);
        })
        .RequireAuthorization(p => p.RequirePermission(PaymentPermissions.StatusManage))
        .WithName("CancelTenantPayment");
    }
}
