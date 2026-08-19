using Knight.Infrastructure.ControlPlane.Security;
using Microsoft.Extensions.Options;

namespace Knight.Api.Ingest;

/// <summary>
/// Serves feature artifacts to the agents fetching them.
///
/// This is the filesystem package store's read side. In a deployment the object
/// store answers these requests directly through a pre-signed URL and KNIGHT is
/// never in the data path; this endpoint exists so that development and the
/// integration suite exercise the same fetch-verify-install sequence without
/// needing object storage running.
///
/// The URL carries an expiry and this checks it. That check is what makes the
/// minted URL a short-lived grant rather than a permanent public link to signed
/// code — and it is why the URL is never stored anywhere.
///
/// The artifact is not a secret: it is verified by digest and signature at the
/// other end, so an attacker who fetches one learns nothing they could not learn
/// by being a customer. The expiry limits exposure, it does not carry the
/// security of the delivery model — the signature does.
/// </summary>
public static class ArtifactEndpoints
{
    public static void MapArtifactEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/artifacts/{reference}", (
            string reference,
            long? expires,
            IOptions<FeatureArtifactOptions> options,
            TimeProvider clock) =>
        {
            if (expires is null || DateTimeOffset.FromUnixTimeSeconds(expires.Value) < clock.GetUtcNow())
            {
                return Results.Problem(
                    title: "This artifact link has expired.",
                    statusCode: StatusCodes.Status410Gone);
            }

            var root = Path.GetFullPath(options.Value.ArtifactRoot);

            // The reference comes off the URL, so it is untrusted input being
            // turned into a file path. Anything that resolves outside the
            // artifact root is refused rather than normalised.
            if (reference.Contains("..", StringComparison.Ordinal) ||
                Path.IsPathRooted(reference))
            {
                return Results.NotFound();
            }

            var path = Path.GetFullPath(Path.Combine(root, reference));

            if (!path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal) || !File.Exists(path))
            {
                return Results.NotFound();
            }

            return Results.File(path, "application/zip", Path.GetFileName(path));
        })
        .AllowAnonymous()
        .WithTags("Artifacts")
        .WithSummary("Serves a feature artifact to an agent holding an unexpired link.");
    }
}
