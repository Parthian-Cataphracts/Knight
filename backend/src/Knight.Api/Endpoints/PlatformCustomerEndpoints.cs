using Customer;
using Customer.Domain;
using Microsoft.AspNetCore.Mvc;
using Knight.Contracts.Common;
using Knight.Contracts.Customer;

namespace Knight.Api.Endpoints;

public static class PlatformCustomerEndpoints
{
    public static void MapPlatformCustomerEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/platform/tenants/{tenantId:guid}/customers")
            .RequireAuthorization("PlatformAdminOnly")
            .RequireRateLimiting("platform")
            .WithTags("Platform Customers");

        group.MapGet("/", async (
            Guid tenantId,
            int? page,
            int? pageSize,
            string? search,
            string? status,
            ICustomerManagementService service,
            CancellationToken ct) =>
        {
            var p = page ?? 1;
            var ps = pageSize ?? 20;

            var filter = new CustomerListFilter(search, CustomerEndpointSupport.ParseStatus(status));
            var (items, totalCount) = await service.ListAsync(tenantId, p, ps, filter, ct);

            var responses = items.Select(CustomerEndpointSupport.ToResponse).ToArray();
            return Results.Ok(PagedResponse<CustomerResponse>.Create(responses, p, ps, totalCount));
        })
        .WithName("PlatformListCustomers");

        group.MapGet("/{customerId:guid}", async (
            Guid tenantId,
            Guid customerId,
            ICustomerManagementService service,
            CancellationToken ct) =>
        {
            var customer = await service.GetByIdAsync(tenantId, customerId, ct);
            if (customer is null)
            {
                return Results.NotFound();
            }

            return Results.Ok(CustomerEndpointSupport.ToResponse(customer));
        })
        .WithName("PlatformGetCustomer");

        group.MapPost("/", async (
            Guid tenantId,
            [FromBody] CreateCustomerRequest request,
            ICustomerManagementService service,
            CancellationToken ct) =>
        {
            var input = new CreateCustomerInput(request.DisplayName, request.Phone, request.Email);
            var customer = await service.CreateAsync(tenantId, input, ct);

            return Results.Created($"/api/platform/tenants/{tenantId}/customers/{customer.Id}", CustomerEndpointSupport.ToResponse(customer));
        })
        .WithName("PlatformCreateCustomer");

        group.MapPut("/{customerId:guid}", async (
            Guid tenantId,
            Guid customerId,
            [FromBody] UpdateCustomerRequest request,
            ICustomerManagementService service,
            CancellationToken ct) =>
        {
            var input = new UpdateCustomerInput(request.DisplayName, request.Phone, request.Email);
            var customer = await service.UpdateAsync(tenantId, customerId, input, ct);

            return Results.Ok(CustomerEndpointSupport.ToResponse(customer));
        })
        .WithName("PlatformUpdateCustomer");

        group.MapPost("/{customerId:guid}/archive", async (
            Guid tenantId,
            Guid customerId,
            ICustomerManagementService service,
            CancellationToken ct) =>
        {
            var customer = await service.ArchiveAsync(tenantId, customerId, ct);
            return Results.Ok(CustomerEndpointSupport.ToResponse(customer));
        })
        .WithName("PlatformArchiveCustomer");

        group.MapPost("/{customerId:guid}/restore", async (
            Guid tenantId,
            Guid customerId,
            ICustomerManagementService service,
            CancellationToken ct) =>
        {
            var customer = await service.RestoreAsync(tenantId, customerId, ct);
            return Results.Ok(CustomerEndpointSupport.ToResponse(customer));
        })
        .WithName("PlatformRestoreCustomer");
    }
}
