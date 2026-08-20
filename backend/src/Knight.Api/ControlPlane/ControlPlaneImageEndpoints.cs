using AccessControl.Domain;
using FeatureRegistry;
using FeatureRegistry.Domain;
using Knight.Application.Abstractions.ControlPlane;
using Knight.Contracts.ControlPlane;

namespace Knight.Api.ControlPlane;

/// <summary>
/// Uploading a signed package, and the base store image registry
/// (docs/store-provisioning.md §3).
///
/// The upload endpoint is what lets a Feature version or a base image be
/// published from the dashboard instead of by hand. It accepts an
/// **already-signed** package: the signature is made offline by
/// <c>knight_package.py</c>, so no signing key is ever present in the web
/// application, and this endpoint could not sign anything if it wanted to
/// (TODO.md phase 9, decided 2026-08-20).
///
/// The digest returned is computed from the stored bytes, not taken from the
/// uploader. The publish request that follows declares that digest, and publish
/// verifies the signature against it — a chain that only means something if the
/// middle link is KNIGHT's own arithmetic.
/// </summary>
public static class ControlPlaneImageEndpoints
{
    /// <summary>
    /// Packages are wheels and archives, not videos. Generous enough for a large
    /// Feature and small enough that a mistaken upload fails fast rather than
    /// filling a disk.
    /// </summary>
    private const long MaxUploadBytes = 256L * 1024 * 1024;

    public static void MapControlPlaneImageEndpoints(this IEndpointRouteBuilder endpoints)
    {
        MapUploads(endpoints);
        MapImages(endpoints);
    }

    private static void MapUploads(IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v1/artifacts")
            .RequireAuthorization(ControlPlaneAuthorizationExtensions.UserPolicy)
            .RequireRateLimiting("control-plane")
            .WithTags("Artifacts");

        group.MapPost("/", async (
            IFormFile file,
            IFeatureArtifactStore artifacts,
            CancellationToken cancellationToken) =>
        {
            if (file.Length is 0)
            {
                return ValidationProblem("file", "The uploaded package is empty.");
            }

            if (file.Length > MaxUploadBytes)
            {
                return ValidationProblem(
                    "file",
                    $"The package is larger than the {MaxUploadBytes / (1024 * 1024)}MB limit.");
            }

            await using var content = file.OpenReadStream();
            var stored = await artifacts.SaveAsync(file.FileName, content, cancellationToken);

            return Results.Ok(new ArtifactUploadResponse
            {
                PackageReference = stored.PackageReference,
                Digest = stored.Digest,
                SizeBytes = stored.SizeBytes,
            });
        })
        .DisableAntiforgery()
        .RequirePermission(ControlPlanePermissions.FeaturePublish)
        .WithSummary("Stores an already-signed package and answers what it hashes to.");
    }

    private static void MapImages(IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v1/store-images")
            .RequireAuthorization(ControlPlaneAuthorizationExtensions.UserPolicy)
            .RequireRateLimiting("control-plane")
            .WithTags("Store images");

        group.MapGet("/", async (IStoreImageService service, CancellationToken cancellationToken) =>
            Results.Ok(new { items = (await service.ListAsync(cancellationToken)).Select(ToResponse).ToArray() }))
            .RequirePermission(ControlPlanePermissions.FeatureView);

        group.MapGet("/{imageId:guid}", async (
            Guid imageId,
            IStoreImageService service,
            CancellationToken cancellationToken) =>
        {
            var image = await service.GetAsync(imageId, cancellationToken);
            return image is null ? Results.NotFound() : Results.Ok(ToResponse(image));
        }).RequirePermission(ControlPlanePermissions.FeatureView);

        group.MapPost("/", async (
            CreateStoreImageRequest request,
            IStoreImageService service,
            CancellationToken cancellationToken) =>
        {
            var image = await service.CreateAsync(
                new PublishStoreImageInput(
                    request.Version,
                    request.StoreVersion,
                    request.PackageReference,
                    request.ArtifactDigest,
                    request.Signature,
                    request.SigningKeyId,
                    request.ReleaseNotes),
                cancellationToken);

            return Results.Created($"/api/v1/store-images/{image.Id}", ToResponse(image));
        }).RequirePermission(ControlPlanePermissions.FeaturePublish);

        group.MapPost("/{imageId:guid}/publish", async (
            Guid imageId,
            IStoreImageService service,
            CancellationToken cancellationToken) =>
            Results.Ok(ToResponse(await service.PublishAsync(imageId, cancellationToken))))
            .RequirePermission(ControlPlanePermissions.FeaturePublish);

        group.MapPost("/{imageId:guid}/yank", async (
            Guid imageId,
            YankStoreImageRequest request,
            IStoreImageService service,
            CancellationToken cancellationToken) =>
            Results.Ok(ToResponse(await service.YankAsync(imageId, request.Reason, cancellationToken))))
            .RequirePermission(ControlPlanePermissions.FeatureYank);
    }

    private static StoreImageResponse ToResponse(StoreImage image) => new()
    {
        Id = image.Id,
        Version = image.Version,
        StoreVersion = image.StoreVersion,
        Status = image.Status.ToString(),
        ArtifactDigest = image.ArtifactDigest,
        ArtifactSizeBytes = image.ArtifactSizeBytes,
        SigningKeyId = image.SigningKeyId,
        ReleaseNotes = image.ReleaseNotes,
        CreatedAt = image.CreatedAt,
        PublishedAt = image.PublishedAt,
        YankedAt = image.YankedAt,
        YankReason = image.YankReason,
    };

    private static IResult ValidationProblem(string field, string message) =>
        Results.ValidationProblem(new Dictionary<string, string[]> { [field] = [message] });
}
