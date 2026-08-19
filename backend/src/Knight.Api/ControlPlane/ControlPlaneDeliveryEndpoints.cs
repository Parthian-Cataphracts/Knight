using AccessControl.Domain;
using FeatureDelivery;
using FeatureDelivery.Domain;
using FeatureRegistry;
using Knight.Application.Abstractions.ControlPlane;
using Knight.Contracts.Common;
using Knight.Contracts.ControlPlane;

namespace Knight.Api.ControlPlane;

/// <summary>
/// Feature versions, installations and jobs (docs/api-contracts.md §2,
/// docs/feature-delivery.md).
///
/// The permission split here is the one phase 1 modelled and this phase finally
/// uses. Publishing a version and yanking one are platform business — they change
/// what every customer can receive — so they need <c>feature.publish</c> and
/// <c>feature.yank</c>. Installing into a store is customer business and needs
/// <c>installation.manage</c>. Uninstalling and rolling back are separated again,
/// because both destroy something a retry cannot bring back and neither should
/// come free with the permission to install.
/// </summary>
public static class ControlPlaneDeliveryEndpoints
{
    public static void MapControlPlaneDeliveryEndpoints(this IEndpointRouteBuilder endpoints)
    {
        MapVersions(endpoints);
        MapInstallations(endpoints);
        MapJobs(endpoints);
    }

    // --- Registry: versions -------------------------------------------------

    private static void MapVersions(IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v1/features")
            .RequireAuthorization(ControlPlaneAuthorizationExtensions.UserPolicy)
            .RequireRateLimiting("control-plane")
            .WithTags("Feature versions");

        // Validation is a read: it changes nothing and exists so an author finds
        // out what is wrong before a pipeline run rather than after.
        group.MapPost("/manifest/validate", (
            ManifestValidationRequest request,
            IFeatureVersionService service) =>
        {
            var result = service.ValidateManifest(request.Manifest);

            return Results.Ok(new ManifestValidationResponse(
                result.IsValid,
                result.Slug,
                result.Version,
                [.. result.Errors.Select(error => new ManifestErrorResponse(error.Path, error.Message))]));
        }).RequirePermission(ControlPlanePermissions.FeatureView);

        group.MapGet("/{featureId:guid}/versions", async (
            Guid featureId,
            int? page,
            int? pageSize,
            IFeatureVersionService service,
            CancellationToken cancellationToken) =>
        {
            var result = await service.ListAsync(featureId, page ?? 1, pageSize ?? 25, cancellationToken);

            return Results.Ok(PagedResponse<FeatureVersionResponse>.Create(
                [.. result.Items.Select(DeliveryMapping.ToResponse)],
                result.Page,
                result.PageSize,
                result.TotalCount));
        }).RequirePermission(ControlPlanePermissions.FeatureView);

        group.MapGet("/versions/{versionId:guid}", async (
            Guid versionId,
            IFeatureVersionService service,
            CancellationToken cancellationToken) =>
        {
            var version = await service.GetAsync(versionId, cancellationToken);
            return version is null
                ? Results.NotFound()
                : Results.Ok(version.ToResponse());
        }).RequirePermission(ControlPlanePermissions.FeatureView);

        group.MapPost("/versions", async (
            CreateFeatureVersionRequest request,
            IFeatureVersionService service,
            CancellationToken cancellationToken) =>
        {
            var version = await service.CreateAsync(
                new PublishVersionInput(
                    request.Manifest,
                    request.PackageReference,
                    request.ArtifactDigest,
                    request.Signature,
                    request.SigningKeyId,
                    request.ReleaseNotes),
                cancellationToken);

            return Results.Created($"/api/v1/features/versions/{version.Id}", version.ToResponse());
        }).RequirePermission(ControlPlanePermissions.FeaturePublish);

        group.MapPost("/versions/{versionId:guid}/publish", async (
            Guid versionId,
            IFeatureVersionService service,
            CancellationToken cancellationToken) =>
            Results.Ok((await service.PublishAsync(versionId, cancellationToken)).ToResponse()))
            .RequirePermission(ControlPlanePermissions.FeaturePublish);

        group.MapPost("/versions/{versionId:guid}/yank", async (
            Guid versionId,
            YankVersionRequest request,
            IFeatureVersionService service,
            CancellationToken cancellationToken) =>
            Results.Ok((await service.YankAsync(versionId, request.Reason, cancellationToken)).ToResponse()))
            .RequirePermission(ControlPlanePermissions.FeatureYank);

        // The containment action for a compromised signing key. Deliberately its
        // own endpoint rather than a flag on yank: it is a fleet-wide action and
        // should read like one in the audit log.
        group.MapPost("/signing-keys/{keyId}/revoke", async (
            string keyId,
            YankVersionRequest request,
            IFeatureVersionService service,
            CancellationToken cancellationToken) =>
        {
            var yanked = await service.YankBySigningKeyAsync(keyId, request.Reason, cancellationToken);
            return Results.Ok(new SigningKeyRevocationResponse(keyId, yanked));
        }).RequirePermission(ControlPlanePermissions.FeatureYank);
    }

    // --- Delivery: installations --------------------------------------------

    private static void MapInstallations(IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v1/installations")
            .RequireAuthorization(ControlPlaneAuthorizationExtensions.UserPolicy)
            .RequireRateLimiting("control-plane")
            .WithTags("Installations");

        group.MapGet("/", async (
            int? page,
            int? pageSize,
            Guid? storeId,
            Guid? customerId,
            string? state,
            IFeatureDeliveryService service,
            CancellationToken cancellationToken) =>
        {
            if (!TryParse<InstallationState>(state, out var parsedState))
            {
                return ValidationProblem("state", $"'{state}' is not a recognised installation state.");
            }

            var result = await service.ListInstallationsAsync(
                page ?? 1, pageSize ?? 25, storeId, customerId, parsedState, cancellationToken);

            return Results.Ok(PagedResponse<FeatureInstallationResponse>.Create(
                [.. result.Items.Select(DeliveryMapping.ToResponse)],
                result.Page,
                result.PageSize,
                result.TotalCount));
        }).RequirePermission(ControlPlanePermissions.InstallationView);

        group.MapGet("/{id:guid}", async (
            Guid id,
            IFeatureDeliveryService service,
            CancellationToken cancellationToken) =>
        {
            var installation = await service.GetInstallationAsync(id, cancellationToken);
            return installation is null
                ? Results.NotFound()
                : Results.Ok(installation.ToResponse());
        }).RequirePermission(ControlPlanePermissions.InstallationView);

        // The dry run behind the dashboard's install preview: what would happen,
        // in what order, and whether any of it needs an irreversible migration.
        group.MapPost("/plan", async (
            InstallFeatureRequest request,
            IFeatureDeliveryService service,
            CancellationToken cancellationToken) =>
            Results.Ok((await service.PreviewAsync(request.StoreId, request.Slug, request.VersionRange, cancellationToken)).ToResponse()))
            .RequirePermission(ControlPlanePermissions.InstallationView);

        group.MapPost("/install", async (
            InstallFeatureRequest request,
            IFeatureDeliveryService service,
            HttpContext http,
            CancellationToken cancellationToken) =>
        {
            var result = await service.InstallAsync(
                new InstallFeatureInput(request.StoreId, request.Slug, request.VersionRange, IdempotencyKey(http)),
                cancellationToken);

            return Results.Ok(result.ToResponse());
        }).RequirePermission(ControlPlanePermissions.InstallationManage);

        group.MapPost("/upgrade", async (
            InstallFeatureRequest request,
            IFeatureDeliveryService service,
            HttpContext http,
            CancellationToken cancellationToken) =>
        {
            var result = await service.UpgradeAsync(
                new InstallFeatureInput(request.StoreId, request.Slug, request.VersionRange, IdempotencyKey(http)),
                cancellationToken);

            return Results.Ok(result.ToResponse());
        }).RequirePermission(ControlPlanePermissions.InstallationManage);

        group.MapPost("/enable", async (
            InstallationActionRequest request,
            IFeatureDeliveryService service,
            CancellationToken cancellationToken) =>
            Results.Ok((await service.EnableAsync(request.StoreId, request.FeatureId, cancellationToken)).ToResponse()))
            .RequirePermission(ControlPlanePermissions.InstallationManage);

        group.MapPost("/disable", async (
            InstallationActionRequest request,
            IFeatureDeliveryService service,
            CancellationToken cancellationToken) =>
            Results.Ok((await service.DisableAsync(
                    request.StoreId,
                    request.FeatureId,
                    request.Reason ?? "Disabled from the dashboard.",
                    cancellationToken)).ToResponse()))
            .RequirePermission(ControlPlanePermissions.InstallationManage);

        group.MapPost("/uninstall", async (
            InstallationActionRequest request,
            IFeatureDeliveryService service,
            CancellationToken cancellationToken) =>
            Results.Ok((await service.UninstallAsync(request.StoreId, request.FeatureId, cancellationToken)).ToResponse()))
            .RequirePermission(ControlPlanePermissions.InstallationUninstall);

        group.MapPost("/rollback", async (
            InstallationActionRequest request,
            IFeatureDeliveryService service,
            CancellationToken cancellationToken) =>
            Results.Ok((await service.RollbackAsync(request.StoreId, request.FeatureId, cancellationToken)).ToResponse()))
            .RequirePermission(ControlPlanePermissions.InstallationRollback);

        group.MapPut("/configuration", async (
            ConfigureFeatureRequest request,
            IFeatureDeliveryService service,
            CancellationToken cancellationToken) =>
            Results.Ok((await service.ConfigureAsync(
                    request.StoreId,
                    request.FeatureId,
                    request.Values,
                    request.Secrets ?? new Dictionary<string, string>(),
                    cancellationToken)).ToResponse()))
            .RequirePermission(ControlPlanePermissions.InstallationManage);
    }

    // --- Delivery: jobs ------------------------------------------------------

    private static void MapJobs(IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v1/jobs")
            .RequireAuthorization(ControlPlaneAuthorizationExtensions.UserPolicy)
            .RequireRateLimiting("control-plane")
            .WithTags("Jobs");

        group.MapGet("/", async (
            int? page,
            int? pageSize,
            Guid? storeId,
            Guid? customerId,
            string? state,
            IFeatureDeliveryService service,
            CancellationToken cancellationToken) =>
        {
            if (!TryParse<JobState>(state, out var parsedState))
            {
                return ValidationProblem("state", $"'{state}' is not a recognised job state.");
            }

            var result = await service.ListJobsAsync(
                page ?? 1, pageSize ?? 25, storeId, customerId, parsedState, cancellationToken);

            return Results.Ok(PagedResponse<FeatureJobResponse>.Create(
                [.. result.Items.Select(DeliveryMapping.ToResponse)],
                result.Page,
                result.PageSize,
                result.TotalCount));
        }).RequirePermission(ControlPlanePermissions.JobView);

        group.MapGet("/{id:guid}", async (
            Guid id,
            IFeatureDeliveryService service,
            CancellationToken cancellationToken) =>
        {
            var job = await service.GetJobAsync(id, cancellationToken);
            return job is null ? Results.NotFound() : Results.Ok(job.ToDetailResponse());
        }).RequirePermission(ControlPlanePermissions.JobView);

        group.MapPost("/{id:guid}/cancel", async (
            Guid id,
            YankVersionRequest request,
            IFeatureDeliveryService service,
            CancellationToken cancellationToken) =>
            Results.Ok((await service.CancelJobAsync(id, request.Reason, cancellationToken)).ToResponse()))
            .RequirePermission(ControlPlanePermissions.JobManage);
    }

    /// <summary>
    /// The caller's idempotency key, if it sent one.
    ///
    /// Without it a retried install after a timeout would queue a second job for
    /// the same feature, which the store would then run twice.
    /// </summary>
    private static string? IdempotencyKey(HttpContext http) =>
        http.Request.Headers.TryGetValue("Idempotency-Key", out var values) ? values.ToString() : null;

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
}
