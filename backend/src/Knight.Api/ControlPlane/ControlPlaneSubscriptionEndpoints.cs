using AccessControl.Domain;
using Knight.Application.Abstractions.ControlPlane;
using Knight.Application.Abstractions.Time;
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
            ILabelReader labels,
            IPricingReader pricing,
            IDateTimeProvider clock,
            CancellationToken cancellationToken) =>
        {
            if (!TryParseStatus(status, out var parsedStatus))
            {
                return ValidationProblem("status", $"'{status}' is not a recognised subscription status.");
            }

            var result = await service.ListAsync(
                new SubscriptionListQuery(page ?? 1, pageSize ?? 25, customerId, parsedStatus),
                cancellationToken);

            var names = await labels.CustomerNamesAsync(
                result.Items.Select(subscription => subscription.CustomerId).Distinct().ToArray(),
                cancellationToken);

            var plans = await labels.PlanNamesAsync(
                result.Items.Select(subscription => subscription.PlanId).Distinct().ToArray(),
                cancellationToken);

            // Priced per row, because two subscriptions on the same plan can
            // have different optional features switched on. The list page is
            // bounded, and the calculator reads a price list the request has
            // already loaded.
            var priced = new Dictionary<Guid, QuotedPrice>();

            foreach (var subscription in result.Items)
            {
                priced[subscription.Id] = await pricing.QuoteAsync(
                    subscription.PlanId,
                    subscription.EnabledFeatureIds,
                    clock.UtcNow,
                    cancellationToken);
            }

            return Results.Ok(PagedResponse<SubscriptionResponse>.Create(
                result.Items
                    .Select(subscription => ToResponse(subscription, names, plans, priced[subscription.Id]))
                    .ToArray(),
                result.Page,
                result.PageSize,
                result.TotalCount));
        }).RequirePermission(ControlPlanePermissions.SubscriptionView);

        group.MapGet("/{id:guid}", async (
            Guid id,
            ISubscriptionService service,
            ILabelReader labels,
            IPricingReader pricing,
            IDateTimeProvider clock,
            CancellationToken cancellationToken) =>
        {
            var subscription = await service.GetAsync(id, cancellationToken);
            return subscription is null
                ? Results.NotFound()
                : Results.Ok(await DescribeAsync(subscription, labels, pricing, clock, cancellationToken));
        }).RequirePermission(ControlPlanePermissions.SubscriptionView);

        group.MapPost("/", async (
            CreateSubscriptionRequest request,
            ISubscriptionService service,
            ILabelReader labels,
            IPricingReader pricing,
            IDateTimeProvider clock,
            CancellationToken cancellationToken) =>
        {
            var subscription = await service.StartAsync(
                new StartSubscriptionInput(request.CustomerId, request.PlanId, request.FeatureIds, request.AsTrial),
                cancellationToken);

            return Results.Created(
                $"/api/v1/subscriptions/{subscription.Id}",
                await DescribeAsync(subscription, labels, pricing, clock, cancellationToken));
        }).RequirePermission(ControlPlanePermissions.SubscriptionManage);

        group.MapPatch("/{id:guid}", async (
            Guid id,
            ChangePlanRequest request,
            ISubscriptionService service,
            ILabelReader labels,
            IPricingReader pricing,
            IDateTimeProvider clock,
            CancellationToken cancellationToken) =>
            Results.Ok(await DescribeAsync(
                await service.ChangePlanAsync(id, request.PlanId, cancellationToken),
                labels,
                pricing,
                clock,
                cancellationToken)))
            .RequirePermission(ControlPlanePermissions.SubscriptionManage);

        group.MapPut("/{id:guid}/features", async (
            Guid id,
            SetSubscriptionFeaturesRequest request,
            ISubscriptionService service,
            ILabelReader labels,
            IPricingReader pricing,
            IDateTimeProvider clock,
            CancellationToken cancellationToken) =>
            Results.Ok(await DescribeAsync(
                await service.SetFeaturesAsync(id, request.FeatureIds, cancellationToken),
                labels,
                pricing,
                clock,
                cancellationToken)))
            .RequirePermission(ControlPlanePermissions.SubscriptionManage);

        group.MapPost("/{id:guid}/cancel", async (
            Guid id,
            ISubscriptionService service,
            ILabelReader labels,
            IPricingReader pricing,
            IDateTimeProvider clock,
            CancellationToken cancellationToken) =>
            Results.Ok(await DescribeAsync(await service.CancelAsync(id, cancellationToken), labels, pricing, clock, cancellationToken)))
            .RequirePermission(ControlPlanePermissions.SubscriptionManage);

        group.MapPost("/{id:guid}/suspend", async (
            Guid id,
            ISubscriptionService service,
            ILabelReader labels,
            IPricingReader pricing,
            IDateTimeProvider clock,
            CancellationToken cancellationToken) =>
            Results.Ok(await DescribeAsync(await service.SuspendAsync(id, cancellationToken), labels, pricing, clock, cancellationToken)))
            .RequirePermission(ControlPlanePermissions.SubscriptionManage);

        group.MapPost("/{id:guid}/activate", async (
            Guid id,
            ISubscriptionService service,
            ILabelReader labels,
            IPricingReader pricing,
            IDateTimeProvider clock,
            CancellationToken cancellationToken) =>
            Results.Ok(await DescribeAsync(await service.ActivateAsync(id, cancellationToken), labels, pricing, clock, cancellationToken)))
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
        {
            var entitlements = (await service.ResolveForCustomerAsync(customerId, includeInactive ?? false, cancellationToken))
                .Select(ToResponse)
                .ToArray();

            return Results.Ok(PagedResponse<EntitlementResponse>.Create(
                entitlements,
                1,
                entitlements.Length,
                entitlements.Length));
        }).RequirePermission(ControlPlanePermissions.FeatureView);

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

    /// <summary>Resolves the labels for one subscription returned on its own.</summary>
    private static async Task<SubscriptionResponse> DescribeAsync(
        Subscription subscription,
        ILabelReader labels,
        IPricingReader pricing,
        IDateTimeProvider clock,
        CancellationToken cancellationToken)
    {
        var names = await labels.CustomerNamesAsync([subscription.CustomerId], cancellationToken);
        var plans = await labels.PlanNamesAsync([subscription.PlanId], cancellationToken);

        var priced = await pricing.QuoteAsync(
            subscription.PlanId,
            subscription.EnabledFeatureIds,
            clock.UtcNow,
            cancellationToken);

        return ToResponse(subscription, names, plans, priced);
    }

    internal static SubscriptionResponse ToResponse(
        Subscription subscription,
        IReadOnlyDictionary<Guid, string> customerNames,
        IReadOnlyDictionary<Guid, (string Key, string Name)> plans,
        QuotedPrice priced) => new()
        {
            MonthlyTotal = priced.Subtotal,
            Currency = priced.Currency,
            Id = subscription.Id,
            CustomerId = subscription.CustomerId,
            CustomerName = customerNames.GetValueOrDefault(subscription.CustomerId) ?? string.Empty,
            PlanId = subscription.PlanId,
            PlanKey = plans.TryGetValue(subscription.PlanId, out var plan) ? plan.Key : string.Empty,
            PlanName = plans.TryGetValue(subscription.PlanId, out var named) ? named.Name : string.Empty,
            OptionalFeatures = subscription.EnabledFeatureIds.Count,
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
