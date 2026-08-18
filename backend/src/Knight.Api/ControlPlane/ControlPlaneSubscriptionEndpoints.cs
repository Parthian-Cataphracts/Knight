using AccessControl.Domain;
using Knight.Contracts.Common;
using Knight.Contracts.ControlPlane;
using Subscriptions;
using Subscriptions.Domain;

namespace Knight.Api.ControlPlane;

/// <summary>
/// Subscriptions, quotes and entitlements (docs/api-contracts.md section 2).
///
/// The quote endpoint has no side effects by design: the dashboard shows the
/// customer what a change would cost before they commit to it, and it must agree
/// with the invoice, so both go through the same calculator.
/// </summary>
public static class ControlPlaneSubscriptionEndpoints
{
    public static void MapControlPlaneSubscriptionEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v1/subscriptions")
            .RequireAuthorization(ControlPlaneAuthorizationExtensions.UserPolicy)
            .RequireRateLimiting("control-plane")
            .WithTags("Subscriptions");

        group.MapGet("/", async (
            int? page,
            int? pageSize,
            Guid? customerId,
            string? status,
            ISubscriptionService service,
            CancellationToken cancellationToken) =>
        {
            if (!TryParseStatus(status, out var parsedStatus))
            {
                return ValidationProblem("status", $"'{status}' is not a recognised subscription status.");
            }

            var result = await service.ListAsync(
                new SubscriptionListQuery(page ?? 1, pageSize ?? 25, customerId, parsedStatus),
                cancellationToken);

            return Results.Ok(PagedResponse<SubscriptionResponse>.Create(
                result.Items.Select(ToResponse).ToArray(),
                result.Page,
                result.PageSize,
                result.TotalCount));
        }).RequirePermission(ControlPlanePermissions.SubscriptionView);

        group.MapGet("/{id:guid}", async (Guid id, ISubscriptionService service, CancellationToken cancellationToken) =>
        {
            var subscription = await service.GetAsync(id, cancellationToken);
            return subscription is null ? Results.NotFound() : Results.Ok(ToResponse(subscription));
        }).RequirePermission(ControlPlanePermissions.SubscriptionView);

        group.MapPost("/", async (
            CreateSubscriptionRequest request,
            ISubscriptionService service,
            CancellationToken cancellationToken) =>
        {
            var subscription = await service.StartAsync(
                new StartSubscriptionInput(request.CustomerId, request.PlanId, request.FeatureIds, request.AsTrial),
                cancellationToken);

            return Results.Created($"/api/v1/subscriptions/{subscription.Id}", ToResponse(subscription));
        }).RequirePermission(ControlPlanePermissions.SubscriptionManage);

        group.MapPatch("/{id:guid}", async (
            Guid id,
            ChangePlanRequest request,
            ISubscriptionService service,
            CancellationToken cancellationToken) =>
            Results.Ok(ToResponse(await service.ChangePlanAsync(id, request.PlanId, cancellationToken))))
            .RequirePermission(ControlPlanePermissions.SubscriptionManage);

        group.MapPut("/{id:guid}/features", async (
            Guid id,
            SetSubscriptionFeaturesRequest request,
            ISubscriptionService service,
            CancellationToken cancellationToken) =>
            Results.Ok(ToResponse(await service.SetFeaturesAsync(id, request.FeatureIds, cancellationToken))))
            .RequirePermission(ControlPlanePermissions.SubscriptionManage);

        group.MapPost("/{id:guid}/cancel", async (Guid id, ISubscriptionService service, CancellationToken cancellationToken) =>
            Results.Ok(ToResponse(await service.CancelAsync(id, cancellationToken))))
            .RequirePermission(ControlPlanePermissions.SubscriptionManage);

        group.MapPost("/{id:guid}/suspend", async (Guid id, ISubscriptionService service, CancellationToken cancellationToken) =>
            Results.Ok(ToResponse(await service.SuspendAsync(id, cancellationToken))))
            .RequirePermission(ControlPlanePermissions.SubscriptionManage);

        group.MapPost("/{id:guid}/activate", async (Guid id, ISubscriptionService service, CancellationToken cancellationToken) =>
            Results.Ok(ToResponse(await service.ActivateAsync(id, cancellationToken))))
            .RequirePermission(ControlPlanePermissions.SubscriptionManage);

        group.MapPost("/quote", async (
            QuoteRequestBody request,
            ISubscriptionService service,
            CancellationToken cancellationToken) =>
        {
            var quote = await service.QuoteAsync(request.PlanId, request.FeatureIds, cancellationToken);

            return Results.Ok(new QuoteResponse
            {
                Currency = quote.Currency,
                Subtotal = quote.Subtotal,
                Lines = quote.Lines
                    .Select(line => new QuoteLineResponse
                    {
                        Description = line.Description,
                        FeatureId = line.FeatureId,
                        Quantity = line.Quantity,
                        UnitPrice = line.UnitPrice,
                        Total = line.Total,
                    })
                    .ToArray(),
            });
        }).RequirePermission(ControlPlanePermissions.SubscriptionView);

        MapEntitlements(endpoints);
    }

    private static void MapEntitlements(IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v1/customers/{customerId:guid}/entitlements")
            .RequireAuthorization(ControlPlaneAuthorizationExtensions.UserPolicy)
            .RequireRateLimiting("control-plane")
            .WithTags("Entitlements");

        group.MapGet("/", async (
            Guid customerId,
            bool? includeInactive,
            IEntitlementService service,
            CancellationToken cancellationToken) =>
            Results.Ok((await service.ResolveForCustomerAsync(customerId, includeInactive ?? false, cancellationToken))
                .Select(ToResponse)
                .ToArray()))
            .RequirePermission(ControlPlanePermissions.FeatureView);

        // Side-effect free: this is the question the install-preview dialog asks
        // before it offers a button.
        group.MapGet("/{featureId:guid}/check", async (
            Guid customerId,
            Guid featureId,
            IEntitlementService service,
            CancellationToken cancellationToken) =>
        {
            var decision = await service.CanEntitleAsync(customerId, featureId, cancellationToken);

            return Results.Ok(new EntitlementCheckResponse
            {
                IsAllowed = decision.IsAllowed,
                Refusal = decision.Refusal.ToString(),
                Detail = decision.Detail,
            });
        }).RequirePermission(ControlPlanePermissions.FeatureView);

        // Granting outside a plan is platform business — subscription.manage is
        // the customer's own lever, this one is not.
        group.MapPost("/", async (
            Guid customerId,
            GrantEntitlementRequest request,
            IControlPlanePrincipal principal,
            IEntitlementService service,
            CancellationToken cancellationToken) =>
        {
            if (principal.UserId is not { } actorId)
            {
                return Results.Unauthorized();
            }

            var granted = await service.GrantAsync(customerId, request.FeatureId, actorId, request.ExpiresAt, cancellationToken);
            return Results.Ok(ToResponse(granted));
        }).RequirePermission(ControlPlanePermissions.PlanManage);

        // A POST rather than a DELETE: revoking carries a reason, and the reason
        // is recorded on the entitlement and in the audit trail rather than being
        // optional. Minimal APIs will not infer a body on DELETE at all.
        group.MapPost("/{featureId:guid}/revoke", async (
            Guid customerId,
            Guid featureId,
            RevokeEntitlementRequest request,
            IEntitlementService service,
            CancellationToken cancellationToken) =>
        {
            await service.RevokeAsync(customerId, featureId, request.Reason, cancellationToken);
            return Results.NoContent();
        }).RequirePermission(ControlPlanePermissions.PlanManage);
    }

    private static bool TryParseStatus(string? value, out SubscriptionStatus? status)
    {
        status = null;

        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        if (!Enum.TryParse<SubscriptionStatus>(value, ignoreCase: true, out var parsed))
        {
            return false;
        }

        status = parsed;
        return true;
    }

    private static IResult ValidationProblem(string field, string message) =>
        Results.ValidationProblem(new Dictionary<string, string[]> { [field] = [message] });

    private static EntitlementResponse ToResponse(EntitlementView view) => new()
    {
        FeatureId = view.FeatureId,
        Source = view.Source,
        GrantedAt = view.GrantedAt,
        ExpiresAt = view.ExpiresAt,
        IsActive = view.IsActive,
    };

    internal static SubscriptionResponse ToResponse(Subscription subscription) => new()
    {
        Id = subscription.Id,
        CustomerId = subscription.CustomerId,
        PlanId = subscription.PlanId,
        Status = subscription.Status.ToString(),
        StartedAt = subscription.StartedAt,
        CurrentPeriodStart = subscription.CurrentPeriodStart,
        CurrentPeriodEnd = subscription.CurrentPeriodEnd,
        CancelledAt = subscription.CancelledAt,
        Features = subscription.Features
            .Select(feature => new SubscriptionFeatureResponse
            {
                FeatureId = feature.FeatureId,
                IsEnabled = feature.IsEnabled,
                EnabledAt = feature.EnabledAt,
            })
            .ToArray(),
    };
}
