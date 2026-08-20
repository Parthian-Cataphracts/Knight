using AccessControl.Domain;
using Knight.Contracts.Common;
using Knight.Contracts.ControlPlane;
using Provisioning;
using Provisioning.Domain;
using Stores;

namespace Knight.Api.ControlPlane;

/// <summary>
/// Provisioning and deprovisioning runs, and the backups stores report
/// (docs/store-provisioning.md).
///
/// Reads are available to anyone who may see the store, because a customer
/// watching their own store come up is the normal case. Starting a run needs
/// <c>store.provision</c>, and starting a deprovisioning run needs
/// <c>store.deprovision</c> — a separate, platform-only permission, because that
/// path ends in deleted data and must not be reachable by the same permission
/// that renames a store.
/// </summary>
public static class ControlPlaneProvisioningEndpoints
{
    public static void MapControlPlaneProvisioningEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v1/provisioning")
            .RequireAuthorization(ControlPlaneAuthorizationExtensions.UserPolicy)
            .RequireRateLimiting("control-plane")
            .WithTags("Provisioning");

        group.MapGet("/", async (
            int? page,
            int? pageSize,
            Guid? storeId,
            Guid? customerId,
            string? state,
            IProvisioningService service,
            CancellationToken cancellationToken) =>
        {
            ProvisioningState? parsed = null;

            if (!string.IsNullOrWhiteSpace(state))
            {
                if (!Enum.TryParse<ProvisioningState>(state, ignoreCase: true, out var wanted))
                {
                    return ValidationProblem("state", $"'{state}' is not a recognised provisioning state.");
                }

                parsed = wanted;
            }

            var result = await service.ListAsync(
                new ProvisioningJobQuery(page ?? 1, pageSize ?? 25, storeId, customerId, parsed),
                cancellationToken);

            return Results.Ok(PagedResponse<ProvisioningJobResponse>.Create(
                [.. result.Items.Select(ProvisioningMapping.ToResponse)],
                result.Page,
                result.PageSize,
                result.TotalCount));
        }).RequirePermission(ControlPlanePermissions.StoreView);

        group.MapGet("/{jobId:guid}", async (
            Guid jobId,
            IProvisioningService service,
            CancellationToken cancellationToken) =>
        {
            var job = await service.GetAsync(jobId, cancellationToken);
            return job is null ? Results.NotFound() : Results.Ok(job.ToResponse());
        }).RequirePermission(ControlPlanePermissions.StoreView);

        group.MapPost("/stores/{storeId:guid}", async (
            Guid storeId,
            StartProvisioningRequest? request,
            IProvisioningService service,
            CancellationToken cancellationToken) =>
        {
            var job = await service.StartProvisioningAsync(storeId, request?.IdempotencyKey, cancellationToken);
            return Results.Created($"/api/v1/provisioning/{job.Id}", job.ToResponse());
        }).RequirePermission(ControlPlanePermissions.StoreProvision);

        group.MapPost("/stores/{storeId:guid}/deprovision", async (
            Guid storeId,
            StartProvisioningRequest? request,
            IProvisioningService service,
            CancellationToken cancellationToken) =>
        {
            var job = await service.StartDeprovisioningAsync(storeId, request?.IdempotencyKey, cancellationToken);
            return Results.Created($"/api/v1/provisioning/{job.Id}", job.ToResponse());
        }).RequirePermission(ControlPlanePermissions.StoreDeprovision);

        // Re-evaluating on demand rather than waiting for the coordinator's next
        // pass. Nothing here changes what the run decides — it only asks now.
        group.MapPost("/{jobId:guid}/advance", async (
            Guid jobId,
            IProvisioningService service,
            CancellationToken cancellationToken) =>
            Results.Ok((await service.AdvanceAsync(jobId, cancellationToken)).ToResponse()))
            .RequirePermission(ControlPlanePermissions.StoreProvision);

        group.MapPost("/{jobId:guid}/steps", async (
            Guid jobId,
            CompleteProvisioningStepRequest request,
            IProvisioningService service,
            CancellationToken cancellationToken) =>
            Results.Ok((await service.CompleteManualStepAsync(
                jobId,
                request.Step,
                request.Detail,
                request.BaseImageVersion,
                cancellationToken)).ToResponse()))
            .RequirePermission(ControlPlanePermissions.StoreProvision);

        group.MapPost("/{jobId:guid}/retry", async (
            Guid jobId,
            IProvisioningService service,
            CancellationToken cancellationToken) =>
            Results.Ok((await service.RetryAsync(jobId, cancellationToken)).ToResponse()))
            .RequirePermission(ControlPlanePermissions.StoreProvision);

        group.MapPost("/{jobId:guid}/cancel", async (
            Guid jobId,
            CancelProvisioningRequest request,
            IProvisioningService service,
            CancellationToken cancellationToken) =>
            Results.Ok((await service.CancelAsync(jobId, request.Reason, cancellationToken)).ToResponse()))
            .RequirePermission(ControlPlanePermissions.StoreProvision);

        MapBackups(endpoints);
    }

    private static void MapBackups(IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v1/stores/{storeId:guid}/backups")
            .RequireAuthorization(ControlPlaneAuthorizationExtensions.UserPolicy)
            .RequireRateLimiting("control-plane")
            .WithTags("Provisioning");

        group.MapGet("/", async (
            Guid storeId,
            int? limit,
            IStoreIntegrationService service,
            CancellationToken cancellationToken) =>
        {
            var backups = await service.ListBackupsAsync(storeId, limit ?? 25, cancellationToken);
            return Results.Ok(backups.Select(ProvisioningMapping.ToResponse).ToArray());
        }).RequirePermission(ControlPlanePermissions.StoreView);
    }

    private static IResult ValidationProblem(string field, string message) =>
        Results.ValidationProblem(new Dictionary<string, string[]> { [field] = [message] });
}
