using AccessControl.Domain;
using FeatureRegistry;
using FeatureRegistry.Domain;
using Knight.Application.Abstractions.ControlPlane;
using Knight.Contracts.Common;
using Knight.Contracts.ControlPlane;
using Plans;
using Plans.Domain;

namespace Knight.Api.ControlPlane;

/// <summary>
/// The feature catalogue and the price list (docs/api-contracts.md section 2).
///
/// Both are platform-owned: a customer may read what is on offer, but defining
/// what a plan contains or what it costs is platform business, which is why the
/// write paths need <c>plan.manage</c> and <c>feature.manage</c> — permissions no
/// customer-scoped role can hold.
/// </summary>
public static class ControlPlanePlanEndpoints
{
    public static void MapControlPlanePlanEndpoints(this IEndpointRouteBuilder endpoints)
    {
        MapFeatures(endpoints);
        MapPlans(endpoints);
    }

    private static void MapFeatures(IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v1/features")
            .RequireAuthorization(ControlPlaneAuthorizationExtensions.UserPolicy)
            .RequireRateLimiting("control-plane")
            .WithTags("Features");

        group.MapGet("/", async (
            int? page,
            int? pageSize,
            string? status,
            string? category,
            string? search,
            IFeatureCatalogService service,
            IFeatureUsageReader usage,
            CancellationToken cancellationToken) =>
        {
            if (!TryParse<FeatureStatus>(status, out var parsedStatus))
            {
                return ValidationProblem("status", $"'{status}' is not a recognised feature status.");
            }

            var result = await service.ListAsync(
                new FeatureListQuery(page ?? 1, pageSize ?? 25, parsedStatus, category, search),
                cancellationToken);

            // Plans offering each feature and how many customers hold it, read
            // once for the page rather than once per row.
            var usages = await usage.SummariseAsync(
                result.Items.Select(feature => feature.Id).ToArray(),
                cancellationToken);

            return Results.Ok(PagedResponse<FeatureResponse>.Create(
                result.Items.Select(feature => ToResponse(feature, usages.GetValueOrDefault(feature.Id))).ToArray(),
                result.Page,
                result.PageSize,
                result.TotalCount));
        }).RequirePermission(ControlPlanePermissions.FeatureView);

        group.MapGet("/{id:guid}", async (
            Guid id,
            IFeatureCatalogService service,
            IFeatureUsageReader usage,
            CancellationToken cancellationToken) =>
        {
            var feature = await service.GetAsync(id, cancellationToken);
            if (feature is null)
            {
                return Results.NotFound();
            }

            var usages = await usage.SummariseAsync([feature.Id], cancellationToken);
            return Results.Ok(ToResponse(feature, usages.GetValueOrDefault(feature.Id)));
        }).RequirePermission(ControlPlanePermissions.FeatureView);

        group.MapPost("/", async (CreateFeatureRequest request, IFeatureCatalogService service, CancellationToken cancellationToken) =>
        {
            var feature = await service.CreateAsync(
                new CreateFeatureInput(
                    request.Slug,
                    request.Name,
                    request.Description,
                    request.Category,
                    request.IsOptional,
                    request.RequiresDedicatedInfrastructure),
                cancellationToken);

            return Results.Created($"/api/v1/features/{feature.Id}", ToResponse(feature));
        }).RequirePermission(ControlPlanePermissions.FeatureManage);

        group.MapPatch("/{id:guid}", async (
            Guid id,
            UpdateFeatureRequest request,
            IFeatureCatalogService service,
            CancellationToken cancellationToken) =>
            Results.Ok(ToResponse(await service.UpdateAsync(
                id,
                new UpdateFeatureInput(request.Name, request.Description, request.Category),
                cancellationToken))))
            .RequirePermission(ControlPlanePermissions.FeatureManage);

        // Publishing a feature identity makes it sellable. Shipping executable
        // code under it is a separate act with a separate permission, and it
        // arrives with the registry in phase 3.5.
        group.MapPost("/{id:guid}/publish", async (Guid id, IFeatureCatalogService service, CancellationToken cancellationToken) =>
            Results.Ok(ToResponse(await service.PublishAsync(id, cancellationToken))))
            .RequirePermission(ControlPlanePermissions.FeaturePublish);

        group.MapPost("/{id:guid}/deprecate", async (Guid id, IFeatureCatalogService service, CancellationToken cancellationToken) =>
            Results.Ok(ToResponse(await service.DeprecateAsync(id, cancellationToken))))
            .RequirePermission(ControlPlanePermissions.FeatureManage);

        group.MapPost("/{id:guid}/withdraw", async (Guid id, IFeatureCatalogService service, CancellationToken cancellationToken) =>
            Results.Ok(ToResponse(await service.WithdrawAsync(id, cancellationToken))))
            .RequirePermission(ControlPlanePermissions.FeatureYank);
    }

    private static void MapPlans(IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v1/plans")
            .RequireAuthorization(ControlPlaneAuthorizationExtensions.UserPolicy)
            .RequireRateLimiting("control-plane")
            .WithTags("Plans");

        group.MapGet("/", async (
            bool? includeInactive,
            IPlanService service,
            IFeatureCatalogReader features,
            IPlanSubscriberReader subscribers,
            CancellationToken cancellationToken) =>
        {
            var plans = await service.ListAsync(includeInactive ?? false, cancellationToken);
            var items = await PlanDescriptions.DescribeAsync(plans, features, subscribers, cancellationToken);

            // Paged envelope like every other collection: the dashboard reads
            // them all through one client that expects "items".
            return Results.Ok(PagedResponse<PlanResponse>.Create(items, 1, items.Count, items.Count));
        }).RequirePermission(ControlPlanePermissions.PlanView);

        group.MapGet("/{id:guid}", async (
            Guid id,
            IPlanService service,
            IFeatureCatalogReader features,
            IPlanSubscriberReader subscribers,
            CancellationToken cancellationToken) =>
        {
            var plan = await service.GetAsync(id, cancellationToken);
            return plan is null
                ? Results.NotFound()
                : Results.Ok(await PlanDescriptions.DescribeAsync(plan, features, subscribers, cancellationToken));
        }).RequirePermission(ControlPlanePermissions.PlanView);

        group.MapPost("/", async (
            CreatePlanRequest request,
            IPlanService service,
            IFeatureCatalogReader features,
            IPlanSubscriberReader subscribers,
            CancellationToken cancellationToken) =>
        {
            var plan = await service.CreateAsync(
                new CreatePlanInput(
                    request.Key,
                    request.Name,
                    request.Description,
                    request.BasePrice,
                    request.Currency,
                    request.SortOrder),
                cancellationToken);

            return Results.Created(
                $"/api/v1/plans/{plan.Id}",
                await PlanDescriptions.DescribeAsync(plan, features, subscribers, cancellationToken));
        }).RequirePermission(ControlPlanePermissions.PlanManage);

        group.MapPatch("/{id:guid}", async (
            Guid id,
            UpdatePlanRequest request,
            IPlanService service,
            IFeatureCatalogReader features,
            IPlanSubscriberReader subscribers,
            CancellationToken cancellationToken) =>
            Results.Ok(await PlanDescriptions.DescribeAsync(
                await service.UpdateAsync(
                    id,
                    new UpdatePlanInput(request.Name, request.Description, request.BasePrice, request.Currency, request.SortOrder),
                    cancellationToken),
                features,
                subscribers,
                cancellationToken)))
            .RequirePermission(ControlPlanePermissions.PlanManage);

        group.MapPost("/{id:guid}/activate", async (
            Guid id,
            IPlanService service,
            IFeatureCatalogReader features,
            IPlanSubscriberReader subscribers,
            CancellationToken cancellationToken) =>
            Results.Ok(await PlanDescriptions.DescribeAsync(
                await service.ActivateAsync(id, cancellationToken),
                features,
                subscribers,
                cancellationToken)))
            .RequirePermission(ControlPlanePermissions.PlanManage);

        group.MapPost("/{id:guid}/deactivate", async (
            Guid id,
            IPlanService service,
            IFeatureCatalogReader features,
            IPlanSubscriberReader subscribers,
            CancellationToken cancellationToken) =>
            Results.Ok(await PlanDescriptions.DescribeAsync(
                await service.DeactivateAsync(id, cancellationToken),
                features,
                subscribers,
                cancellationToken)))
            .RequirePermission(ControlPlanePermissions.PlanManage);

        group.MapPut("/{id:guid}/features", async (
            Guid id,
            SetPlanFeatureRequest request,
            IPlanService service,
            IFeatureCatalogReader features,
            IPlanSubscriberReader subscribers,
            CancellationToken cancellationToken) =>
            Results.Ok(await PlanDescriptions.DescribeAsync(
                await service.SetFeatureAsync(
                    id,
                    new SetPlanFeatureInput(
                        request.FeatureId,
                        request.IsIncluded,
                        request.IsCustomerToggleable,
                        request.PinnedVersionRange),
                    cancellationToken),
                features,
                subscribers,
                cancellationToken)))
            .RequirePermission(ControlPlanePermissions.PlanManage);

        group.MapDelete("/{id:guid}/features/{featureId:guid}", async (
            Guid id,
            Guid featureId,
            IPlanService service,
            IFeatureCatalogReader features,
            IPlanSubscriberReader subscribers,
            CancellationToken cancellationToken) =>
            Results.Ok(await PlanDescriptions.DescribeAsync(
                await service.RemoveFeatureAsync(id, featureId, cancellationToken),
                features,
                subscribers,
                cancellationToken)))
            .RequirePermission(ControlPlanePermissions.PlanManage);

        group.MapPut("/prices", async (SetFeaturePriceRequest request, IPlanService service, CancellationToken cancellationToken) =>
        {
            if (!Enum.TryParse<BillingPeriod>(request.BillingPeriod, ignoreCase: true, out var billingPeriod))
            {
                return ValidationProblem("billingPeriod", $"'{request.BillingPeriod}' is not a recognised billing period.");
            }

            var price = await service.SetPriceAsync(
                new SetFeaturePriceInput(request.FeatureId, request.PlanId, request.Amount, request.Currency, billingPeriod),
                cancellationToken);

            return Results.Ok(ToResponse(price));
        }).RequirePermission(ControlPlanePermissions.PlanManage);

        group.MapGet("/prices/{featureId:guid}", async (
            Guid featureId,
            IPlanService service,
            CancellationToken cancellationToken) =>
        {
            var prices = (await service.ListPricesAsync(featureId, cancellationToken)).Select(ToResponse).ToArray();
            return Results.Ok(PagedResponse<FeaturePriceResponse>.Create(prices, 1, prices.Length, prices.Length));
        }).RequirePermission(ControlPlanePermissions.PlanView);
    }

    private static bool TryParse<TEnum>(string? value, out TEnum? parsed) where TEnum : struct, Enum
    {
        parsed = null;

        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        if (!Enum.TryParse<TEnum>(value, ignoreCase: true, out var result))
        {
            return false;
        }

        parsed = result;
        return true;
    }

    private static IResult ValidationProblem(string field, string message) =>
        Results.ValidationProblem(new Dictionary<string, string[]> { [field] = [message] });

    /// <summary>
    /// <paramref name="usage"/> is null when the caller had no reason to read
    /// it; the response then reports no plans and nobody entitled, which is what
    /// a brand-new feature looks like anyway.
    /// </summary>
    internal static FeatureResponse ToResponse(Feature feature, FeatureUsage? usage = null) => new()
    {
        Plans = usage?.PlanKeys ?? [],
        EntitledCount = usage?.EntitledCount ?? 0,

        // Versions and installations belong to the registry and delivery
        // subsystem (phase 3.5); null says "not knowable yet" rather than zero,
        // which would claim the feature is installed nowhere.
        LatestVersion = null,
        InstallCount = null,
        Id = feature.Id,
        Slug = feature.Slug,
        Name = feature.Name,
        Description = feature.Description,
        Category = feature.Category,
        IsOptional = feature.IsOptional,
        RequiresDedicatedInfrastructure = feature.RequiresDedicatedInfrastructure,
        Status = feature.Status.ToString(),
        CreatedAt = feature.CreatedAt,
        UpdatedAt = feature.UpdatedAt,
    };

    private static FeaturePriceResponse ToResponse(FeaturePrice price) => new()
    {
        Id = price.Id,
        FeatureId = price.FeatureId,
        PlanId = price.PlanId,
        Amount = price.Amount,
        Currency = price.Currency,
        BillingPeriod = price.BillingPeriod.ToString(),
        ValidFrom = price.ValidFrom,
        ValidTo = price.ValidTo,
    };
}
