using Knight.Contracts.ControlPlane;
using Plans.Domain;
using Provisioning;
using Provisioning.Domain;
using Stores;
using Subscriptions;
using Subscriptions.Domain;

namespace Knight.Api.ControlPlane;

/// <summary>
/// The customer's own self-service surface (docs/self-service-saas-plan.md §6).
/// Every route resolves the customer from the authenticated principal and never
/// from the request, and the persistence scope confines every read to that
/// customer regardless — a customer can never reach another's subscription, store
/// or provisioning run.
///
/// Kept apart from the operations dashboard's routes: this is what a merchant sees
/// about their own store, not what an operator sees about everyone's.
/// </summary>
public static class ControlPlaneMeEndpoints
{
    public static void MapControlPlaneMeEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v1/me")
            .RequireAuthorization(ControlPlaneAuthorizationExtensions.UserPolicy)
            .RequireRateLimiting("control-plane")
            .WithTags("Self-Service Me");

        group.MapGet("/subscription", async (
            IControlPlanePrincipal principal,
            ISubscriptionRepository subscriptions,
            IPlanRepository plans,
            CancellationToken cancellationToken) =>
        {
            if (principal.CustomerId is not { } customerId)
            {
                return Forbidden();
            }

            var subscription = await subscriptions.GetActiveForCustomerAsync(customerId, cancellationToken);
            if (subscription is null)
            {
                return Results.NoContent();
            }

            var plan = await plans.GetByIdAsync(subscription.PlanId, cancellationToken);

            return Results.Ok(new MeSubscriptionResponse
            {
                Id = subscription.Id,
                PlanId = subscription.PlanId,
                PlanName = plan?.Name ?? "Plan",
                Status = subscription.Status.ToString().ToLowerInvariant() switch
                {
                    "pastdue" => "past_due",
                    var other => other,
                },
                CurrentPeriodEnd = subscription.CurrentPeriodEnd,
                CancelAtPeriodEnd = subscription.CancelAtPeriodEnd,
                FeatureIds = subscription.EnabledFeatureIds,
            });
        });

        group.MapPost("/subscription/cancel", async (
            IControlPlanePrincipal principal,
            ISubscriptionRepository subscriptions,
            ISubscriptionService service,
            CancellationToken cancellationToken) =>
        {
            if (principal.CustomerId is not { } customerId)
            {
                return Forbidden();
            }

            var subscription = await subscriptions.GetActiveForCustomerAsync(customerId, cancellationToken);
            if (subscription is null)
            {
                return Results.NotFound();
            }

            await service.RequestCancelAtPeriodEndAsync(subscription.Id, cancellationToken);
            return Results.NoContent();
        });

        // Data portability: the customer takes away KNIGHT's whole record of them,
        // self-serve, as a JSON download (hardening backlog P3).
        group.MapGet("/export", async (
            IControlPlanePrincipal principal,
            Knight.Application.Abstractions.ControlPlane.ITenantExportReader export,
            CancellationToken cancellationToken) =>
        {
            if (principal.CustomerId is not { } customerId)
            {
                return Forbidden();
            }

            var document = await export.ExportAsync(customerId, cancellationToken);

            return Results.Json(document, contentType: "application/json", statusCode: StatusCodes.Status200OK);
        });

        group.MapGet("/stores", async (
            IControlPlanePrincipal principal,
            IStoreManagementService stores,
            CancellationToken cancellationToken) =>
        {
            if (principal.CustomerId is not { } customerId)
            {
                return Forbidden();
            }

            var page = await stores.ListAsync(new StoreListQuery(1, 100, customerId, Environment: null, Status: null), cancellationToken);
            return Results.Ok(page.Items.Select(ToStore).ToArray());
        });

        group.MapGet("/stores/{storeId:guid}", async (
            Guid storeId,
            IStoreManagementService stores,
            CancellationToken cancellationToken) =>
        {
            // The customer scope confines this: another customer's store id
            // resolves to null here exactly as if it did not exist.
            var store = await stores.GetAsync(storeId, cancellationToken);
            return store is null ? Results.NotFound() : Results.Ok(ToStore(store));
        });

        group.MapGet("/stores/{storeId:guid}/provisioning", async (
            Guid storeId,
            IStoreManagementService stores,
            IProvisioningService provisioning,
            CancellationToken cancellationToken) =>
        {
            var store = await stores.GetAsync(storeId, cancellationToken);
            if (store is null)
            {
                return Results.NotFound();
            }

            var jobs = await provisioning.ListAsync(
                new ProvisioningJobQuery(1, 1, storeId, CustomerId: null, State: null),
                cancellationToken);
            var job = jobs.Items.FirstOrDefault(item => item.Kind is ProvisioningKind.Provision);

            return Results.Ok(ToProvisioning(storeId, job));
        });
    }

    private static IResult Forbidden() => Results.Problem(
        title: "Only a customer account has a self-service view.",
        statusCode: StatusCodes.Status403Forbidden,
        extensions: new Dictionary<string, object?> { ["errorCode"] = "UNAUTHORIZED_STORE_ACCESS" });

    private static MeStoreResponse ToStore(Stores.Domain.Store store) => new()
    {
        Id = store.Id,
        Name = store.Name,
        Slug = store.Slug,
        PrimaryDomain = store.PrimaryDomain,
        Status = store.Status.ToString().ToLowerInvariant(),
        IntegrationStatus = store.IntegrationStatus.ToString(),
        IsReady = store.Status is Stores.Domain.StoreStatus.Active,
    };

    private static MeProvisioningResponse ToProvisioning(Guid storeId, ProvisioningJob? job)
    {
        if (job is null)
        {
            return new MeProvisioningResponse
            {
                StoreId = storeId,
                State = "none",
                FriendlyStatus = "Your store has not started provisioning yet.",
                PercentComplete = 0,
                Steps = [],
            };
        }

        var total = job.TotalStepCount;
        var completed = job.CompletedStepCount;
        var percent = total == 0 ? 0 : (int)Math.Round(completed * 100.0 / total);

        var (state, friendly) = job.State switch
        {
            ProvisioningState.Succeeded => ("ready", "Your store is ready."),
            ProvisioningState.Failed => ("failed", "Something went wrong bringing your store up; our team has been notified."),
            ProvisioningState.AwaitingOperator => ("awaiting_operator", "Your store needs a quick manual step from our team."),
            _ => ("provisioning", FriendlyStepLabel(job.NextStep())),
        };

        return new MeProvisioningResponse
        {
            StoreId = storeId,
            State = state,
            FriendlyStatus = friendly,
            PercentComplete = job.State is ProvisioningState.Succeeded ? 100 : percent,
            Steps = job.Steps
                .Select(step => new MeProvisioningStepResponse
                {
                    Name = step.Name,
                    Status = step.Status.ToString().ToLowerInvariant(),
                })
                .ToArray(),
        };
    }

    /// <summary>
    /// The internal step turned into a line a merchant can read, never the raw
    /// name (docs/self-service-saas-plan.md §5).
    /// </summary>
    private static string FriendlyStepLabel(string? step) => step switch
    {
        ProvisioningPipeline.Server or ProvisioningPipeline.Instance => "Preparing your store's infrastructure…",
        ProvisioningPipeline.StoreRecord or ProvisioningPipeline.Credentials
            or ProvisioningPipeline.Agent or ProvisioningPipeline.Configuration => "Connecting your store…",
        ProvisioningPipeline.BaseFeatures => "Installing your features…",
        ProvisioningPipeline.DomainAndTls => "Setting up your domain…",
        ProvisioningPipeline.HealthCheck => "Finalizing your store…",
        _ => "Setting up your store…",
    };
}
