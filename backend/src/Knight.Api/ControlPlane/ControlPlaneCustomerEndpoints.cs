using AccessControl.Domain;
using Customers;
using Customers.Domain;
using Knight.Application.Abstractions.ControlPlane;
using Knight.Contracts.Common;
using Knight.Contracts.ControlPlane;
// The legacy store-side module is also named Customer; the control-plane
// aggregate is aliased so the bare name cannot resolve to that namespace.
using ControlPlaneCustomer = Customers.Domain.Customer;

namespace Knight.Api.ControlPlane;

/// <summary>
/// Customer management (docs/api-contracts.md section 2). Isolation is not
/// enforced here — it is enforced in persistence — so a customer-scoped caller
/// listing customers sees only their own, and asking for another by id gets 404
/// rather than 403 (docs/authorization.md section 4).
/// </summary>
public static class ControlPlaneCustomerEndpoints
{
    public static void MapControlPlaneCustomerEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v1/customers")
            .RequireAuthorization(ControlPlaneAuthorizationExtensions.UserPolicy)
            .RequireRateLimiting("control-plane")
            .WithTags("Customers");

        group.MapGet("/", async (
            int? page,
            int? pageSize,
            string? status,
            string? q,
            ICustomerManagementService service,
            ICustomerDirectoryReader directory,
            CancellationToken cancellationToken) =>
        {
            if (!TryParseStatus(status, out var parsedStatus))
            {
                return ValidationProblem("status", $"'{status}' is not a recognised customer status.");
            }

            var result = await service.ListAsync(
                new CustomerListQuery(page ?? 1, pageSize ?? 25, parsedStatus, q),
                cancellationToken);

            // Store count and current plan are read once for the whole page
            // rather than per row.
            var summaries = await directory.SummariseAsync(
                result.Items.Select(customer => customer.Id).ToArray(),
                cancellationToken);

            return Results.Ok(PagedResponse<CustomerResponse>.Create(
                result.Items.Select(customer => ToResponse(customer, summaries.GetValueOrDefault(customer.Id))).ToArray(),
                result.Page,
                result.PageSize,
                result.TotalCount));
        }).RequirePermission(ControlPlanePermissions.CustomerView);

        group.MapGet("/{id:guid}", async (
            Guid id,
            ICustomerManagementService service,
            ICustomerDirectoryReader directory,
            CancellationToken cancellationToken) =>
        {
            var customer = await service.GetAsync(id, cancellationToken);
            if (customer is null)
            {
                return Results.NotFound();
            }

            var summaries = await directory.SummariseAsync([customer.Id], cancellationToken);
            return Results.Ok(ToResponse(customer, summaries.GetValueOrDefault(customer.Id)));
        }).RequirePermission(ControlPlanePermissions.CustomerView);

        group.MapPost("/", async (CreateCustomerRequest request, ICustomerManagementService service, CancellationToken cancellationToken) =>
        {
            var customer = await service.CreateAsync(
                new CreateCustomerInput(request.Name, request.LegalName, request.ContactEmail, request.Phone, request.Notes),
                cancellationToken);

            return Results.Created($"/api/v1/customers/{customer.Id}", ToResponse(customer));
        }).RequirePermission(ControlPlanePermissions.CustomerCreate);

        group.MapPatch("/{id:guid}", async (Guid id, UpdateCustomerRequest request, ICustomerManagementService service, CancellationToken cancellationToken) =>
        {
            var customer = await service.UpdateAsync(
                id,
                new UpdateCustomerInput(request.Name, request.LegalName, request.ContactEmail, request.Phone, request.Notes),
                cancellationToken);

            return Results.Ok(ToResponse(customer));
        }).RequirePermission(ControlPlanePermissions.CustomerUpdate);

        // Its own route rather than a field on the update: a negotiated
        // retention window is a contractual promise, and it wants an audit entry
        // that says so rather than one that says "customer updated".
        group.MapPut("/{id:guid}/retention", async (
            Guid id,
            SetRetentionOverrideRequest request,
            ICustomerManagementService service,
            CancellationToken cancellationToken) =>
            Results.Ok(ToResponse(await service.SetRetentionOverrideAsync(id, request.Days, cancellationToken))))
            .RequirePermission(ControlPlanePermissions.CustomerUpdate);

        group.MapPost("/{id:guid}/activate", async (Guid id, ICustomerManagementService service, CancellationToken cancellationToken) =>
            Results.Ok(ToResponse(await service.ActivateAsync(id, cancellationToken))))
            .RequirePermission(ControlPlanePermissions.CustomerUpdate);

        group.MapPost("/{id:guid}/suspend", async (Guid id, ICustomerManagementService service, CancellationToken cancellationToken) =>
            Results.Ok(ToResponse(await service.SuspendAsync(id, cancellationToken))))
            .RequirePermission(ControlPlanePermissions.CustomerUpdate);

        group.MapPost("/{id:guid}/archive", async (Guid id, ICustomerManagementService service, CancellationToken cancellationToken) =>
            Results.Ok(ToResponse(await service.ArchiveAsync(id, cancellationToken))))
            .RequirePermission(ControlPlanePermissions.CustomerArchive);
    }

    private static bool TryParseStatus(string? value, out CustomerStatus? status)
    {
        status = null;

        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        if (!Enum.TryParse<CustomerStatus>(value, ignoreCase: true, out var parsed))
        {
            return false;
        }

        status = parsed;
        return true;
    }

    private static IResult ValidationProblem(string field, string message) =>
        Results.ValidationProblem(new Dictionary<string, string[]> { [field] = [message] });

    /// <summary>
    /// <paramref name="summary"/> is null only when the caller had no reason to
    /// read it — the counts then report zero and no plan, which is what a
    /// customer with neither looks like anyway.
    /// </summary>
    internal static CustomerResponse ToResponse(ControlPlaneCustomer customer, CustomerSummary? summary = null) => new()
    {
        StoreCount = summary?.StoreCount ?? 0,
        PlanKey = summary?.PlanKey,
        DataRetentionOverrideDays = customer.DataRetentionOverrideDays,
        Id = customer.Id,
        Name = customer.Name,
        LegalName = customer.LegalName,
        ContactEmail = customer.ContactEmail,
        Phone = customer.Phone,
        Status = customer.Status.ToString(),
        Notes = customer.Notes,
        CreatedAt = customer.CreatedAt,
        UpdatedAt = customer.UpdatedAt,
    };
}
