using AccessControl.Domain;
using Billing;
using Billing.Domain;
using Knight.Contracts.Common;
using Knight.Contracts.ControlPlane;
using Knight.Application.Abstractions.ControlPlane;
using Knight.Application.Abstractions.Time;

namespace Knight.Api.ControlPlane;

/// <summary>
/// Invoices and payment records (docs/api-contracts.md section 2).
///
/// A customer may read their own invoices; issuing one, voiding one or recording
/// a payment against one is platform business under <c>billing.manage</c>, which
/// no customer-scoped role holds. Recording a payment is bookkeeping — no money
/// moves through this API.
/// </summary>
public static class ControlPlaneBillingEndpoints
{
    public static void MapControlPlaneBillingEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v1/invoices")
            .RequireAuthorization(ControlPlaneAuthorizationExtensions.UserPolicy)
            .RequireRateLimiting("control-plane")
            .WithTags("Billing");

        group.MapGet("/", async (
            int? page,
            int? pageSize,
            Guid? customerId,
            string? status,
            IBillingService service,
            ILabelReader labels,
            CancellationToken cancellationToken) =>
        {
            if (!TryParseStatus(status, out var parsedStatus))
            {
                return ValidationProblem("status", $"'{status}' is not a recognised invoice status.");
            }

            var result = await service.ListInvoicesAsync(
                new InvoiceListQuery(page ?? 1, pageSize ?? 25, customerId, parsedStatus),
                cancellationToken);

            var names = await labels.CustomerNamesAsync(
                result.Items.Select(invoice => invoice.CustomerId).Distinct().ToArray(),
                cancellationToken);

            return Results.Ok(PagedResponse<InvoiceResponse>.Create(
                result.Items.Select(invoice => ToResponse(invoice, names.GetValueOrDefault(invoice.CustomerId))).ToArray(),
                result.Page,
                result.PageSize,
                result.TotalCount));
        }).RequirePermission(ControlPlanePermissions.BillingView);

        group.MapGet("/{id:guid}", async (
            Guid id,
            IBillingService service,
            ILabelReader labels,
            CancellationToken cancellationToken) =>
        {
            var invoice = await service.GetInvoiceAsync(id, cancellationToken);
            if (invoice is null)
            {
                return Results.NotFound();
            }

            var names = await labels.CustomerNamesAsync([invoice.CustomerId], cancellationToken);
            return Results.Ok(ToResponse(invoice, names.GetValueOrDefault(invoice.CustomerId)));
        }).RequirePermission(ControlPlanePermissions.BillingView);

        group.MapPost("/prepare/{subscriptionId:guid}", async (
            Guid subscriptionId,
            IBillingService service,
            CancellationToken cancellationToken) =>
            Results.Ok(ToResponse(await service.PrepareInvoiceAsync(subscriptionId, cancellationToken))))
            .RequirePermission(ControlPlanePermissions.BillingManage);

        group.MapPost("/{id:guid}/issue", async (Guid id, IBillingService service, CancellationToken cancellationToken) =>
            Results.Ok(ToResponse(await service.IssueInvoiceAsync(id, cancellationToken))))
            .RequirePermission(ControlPlanePermissions.BillingManage);

        group.MapPost("/{id:guid}/void", async (Guid id, IBillingService service, CancellationToken cancellationToken) =>
            Results.Ok(ToResponse(await service.VoidInvoiceAsync(id, cancellationToken))))
            .RequirePermission(ControlPlanePermissions.BillingManage);

        group.MapPost("/{id:guid}/payments", async (
            Guid id,
            RecordPaymentRequest request,
            IBillingService service,
            IDateTimeProvider clock,
            CancellationToken cancellationToken) =>
        {
            if (!Enum.TryParse<PaymentMethod>(request.Method, ignoreCase: true, out var method))
            {
                return ValidationProblem("method", $"'{request.Method}' is not a recognised payment method.");
            }

            var invoice = await service.RecordPaymentAsync(
                id,
                new RecordPaymentInput(request.Amount, request.Currency, method, request.Reference, request.PaidAt ?? clock.UtcNow),
                cancellationToken);

            return Results.Ok(ToResponse(invoice));
        }).RequirePermission(ControlPlanePermissions.BillingManage);

        MapAccounts(endpoints);
    }

    private static void MapAccounts(IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v1/billing-accounts")
            .RequireAuthorization(ControlPlaneAuthorizationExtensions.UserPolicy)
            .RequireRateLimiting("control-plane")
            .WithTags("Billing");

        group.MapGet("/{customerId:guid}", async (
            Guid customerId,
            IBillingService service,
            CancellationToken cancellationToken) =>
        {
            var account = await service.GetAccountAsync(customerId, cancellationToken);
            return account is null ? Results.NotFound() : Results.Ok(ToResponse(account));
        }).RequirePermission(ControlPlanePermissions.BillingView);

        group.MapPut("/", async (
            OpenBillingAccountRequest request,
            IBillingService service,
            CancellationToken cancellationToken) =>
        {
            var account = await service.OpenAccountAsync(
                new OpenBillingAccountInput(request.CustomerId, request.Currency, request.BillingEmail, request.TaxId),
                cancellationToken);

            return Results.Ok(ToResponse(account));
        }).RequirePermission(ControlPlanePermissions.BillingManage);
    }

    private static bool TryParseStatus(string? value, out InvoiceStatus? status)
    {
        status = null;

        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        if (!Enum.TryParse<InvoiceStatus>(value, ignoreCase: true, out var parsed))
        {
            return false;
        }

        status = parsed;
        return true;
    }

    private static IResult ValidationProblem(string field, string message) =>
        Results.ValidationProblem(new Dictionary<string, string[]> { [field] = [message] });

    private static BillingAccountResponse ToResponse(BillingAccount account) => new()
    {
        Id = account.Id,
        CustomerId = account.CustomerId,
        Currency = account.Currency,
        BillingEmail = account.BillingEmail,
        TaxId = account.TaxId,
    };

    internal static InvoiceResponse ToResponse(Invoice invoice, string? customerName = null) => new()
    {
        Id = invoice.Id,
        CustomerId = invoice.CustomerId,
        CustomerName = customerName ?? string.Empty,
        SubscriptionId = invoice.SubscriptionId,
        Number = invoice.Number,
        PeriodStart = invoice.PeriodStart,
        PeriodEnd = invoice.PeriodEnd,
        Subtotal = invoice.Subtotal,
        Tax = invoice.Tax,
        Total = invoice.Total,
        Paid = invoice.PaidAmount,
        Outstanding = invoice.OutstandingAmount,
        Currency = invoice.Currency,
        Status = invoice.Status.ToString(),
        IssuedAt = invoice.IssuedAt,
        DueAt = invoice.DueAt,
        PaidAt = invoice.PaidAt,
        Lines = invoice.Lines
            .Select(line => new InvoiceLineResponse
            {
                Id = line.Id,
                Description = line.Description,
                FeatureId = line.FeatureId,
                Quantity = line.Quantity,
                UnitPrice = line.UnitPrice,
                Total = line.Total,
            })
            .ToArray(),
        Payments = invoice.Payments
            .Select(payment => new PaymentResponse
            {
                Id = payment.Id,
                Amount = payment.Amount,
                Currency = payment.Currency,
                Method = payment.Method.ToString(),
                Reference = payment.Reference,
                PaidAt = payment.PaidAt,
            })
            .ToArray(),
    };
}
