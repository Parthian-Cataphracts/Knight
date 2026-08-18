using Customer;
using Customer.Domain;
using Microsoft.AspNetCore.Mvc;
using Knight.Api.Authorization;
using Knight.Application.Abstractions.Tenancy;
using Knight.Contracts.Common;
using Knight.Contracts.Customer;

namespace Knight.Api.Endpoints;

public static class TenantCustomerEndpoints
{
    public static void MapTenantCustomerEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/tenant/customers")
            .RequireAuthorization("TenantUserOnly")
            .RequireFeature(CustomerFeature.Key)
            .RequireRateLimiting("platform")
            .WithTags("Tenant Customers");

        group.MapGet("/", async (
            int? page,
            int? pageSize,
            string? search,
            string? status,
            ITenantContext tenantContext,
            ICustomerManagementService service,
            CancellationToken ct) =>
        {
            var tenantId = TenantStaffEndpoints.RequireTenant(tenantContext);
            var p = page ?? 1;
            var ps = pageSize ?? 20;

            var filter = new CustomerListFilter(search, CustomerEndpointSupport.ParseStatus(status));
            var (items, totalCount) = await service.ListAsync(tenantId, p, ps, filter, ct);

            var responses = items.Select(CustomerEndpointSupport.ToResponse).ToArray();
            return Results.Ok(PagedResponse<CustomerResponse>.Create(responses, p, ps, totalCount));
        })
        .RequireAuthorization(p => p.RequirePermission(CustomerPermissions.CustomersView));

        group.MapGet("/{customerId:guid}", async (
            Guid customerId,
            ITenantContext tenantContext,
            ICustomerManagementService service,
            CancellationToken ct) =>
        {
            var tenantId = TenantStaffEndpoints.RequireTenant(tenantContext);
            var customer = await service.GetByIdAsync(tenantId, customerId, ct);
            if (customer is null)
            {
                return Results.NotFound();
            }

            return Results.Ok(CustomerEndpointSupport.ToResponse(customer));
        })
        .RequireAuthorization(p => p.RequirePermission(CustomerPermissions.CustomersView));

        group.MapPost("/", async (
            [FromBody] CreateCustomerRequest request,
            ITenantContext tenantContext,
            ICustomerManagementService service,
            CancellationToken ct) =>
        {
            var tenantId = TenantStaffEndpoints.RequireTenant(tenantContext);
            var input = new CreateCustomerInput(request.DisplayName, request.Phone, request.Email);
            var customer = await service.CreateAsync(tenantId, input, ct);

            return Results.Created($"/api/tenant/customers/{customer.Id}", CustomerEndpointSupport.ToResponse(customer));
        })
        .RequireAuthorization(p => p.RequirePermission(CustomerPermissions.CustomersCreate));

        group.MapPut("/{customerId:guid}", async (
            Guid customerId,
            [FromBody] UpdateCustomerRequest request,
            ITenantContext tenantContext,
            ICustomerManagementService service,
            CancellationToken ct) =>
        {
            var tenantId = TenantStaffEndpoints.RequireTenant(tenantContext);
            var input = new UpdateCustomerInput(request.DisplayName, request.Phone, request.Email);
            var customer = await service.UpdateAsync(tenantId, customerId, input, ct);

            return Results.Ok(CustomerEndpointSupport.ToResponse(customer));
        })
        .RequireAuthorization(p => p.RequirePermission(CustomerPermissions.CustomersUpdate));

        group.MapPost("/{customerId:guid}/archive", async (
            Guid customerId,
            ITenantContext tenantContext,
            ICustomerManagementService service,
            CancellationToken ct) =>
        {
            var tenantId = TenantStaffEndpoints.RequireTenant(tenantContext);
            var customer = await service.ArchiveAsync(tenantId, customerId, ct);
            return Results.Ok(CustomerEndpointSupport.ToResponse(customer));
        })
        .RequireAuthorization(p => p.RequirePermission(CustomerPermissions.CustomersArchive));

        group.MapPost("/{customerId:guid}/restore", async (
            Guid customerId,
            ITenantContext tenantContext,
            ICustomerManagementService service,
            CancellationToken ct) =>
        {
            var tenantId = TenantStaffEndpoints.RequireTenant(tenantContext);
            var customer = await service.RestoreAsync(tenantId, customerId, ct);
            return Results.Ok(CustomerEndpointSupport.ToResponse(customer));
        })
        .RequireAuthorization(p => p.RequirePermission(CustomerPermissions.CustomersRestore));
    }
}
