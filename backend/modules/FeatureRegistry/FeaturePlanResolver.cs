using FeatureRegistry.Domain;
using Knight.Application.Abstractions.ControlPlane;
using Knight.Domain.Versioning;

namespace FeatureRegistry;

/// <summary>
/// Exposes the registry's dependency resolver to the delivery engine.
///
/// The resolver itself is a pure function over a registry snapshot; this class
/// is only the part that fetches the snapshot and translates between the
/// registry's own types and the contract types the delivery engine sees. Keeping
/// the translation here — rather than letting delivery reach into the registry —
/// is what lets the two modules stay independent of each other while the harder
/// half of the problem stays where the knowledge is.
/// </summary>
internal sealed class FeaturePlanResolver : IFeaturePlanResolver
{
    private readonly IFeatureVersionRepository _versions;

    public FeaturePlanResolver(IFeatureVersionRepository versions)
    {
        _versions = versions;
    }

    public Task<FeaturePlan> ResolveAsync(
        string slug,
        string? versionRange,
        FeaturePlanContext context,
        CancellationToken cancellationToken,
        bool moveForward = false)
        => ResolveManyAsync([(slug, versionRange)], context, cancellationToken, moveForward);

    public async Task<FeaturePlan> ResolveManyAsync(
        IReadOnlyList<(string Slug, string? VersionRange)> roots,
        FeaturePlanContext context,
        CancellationToken cancellationToken,
        bool moveForward = false)
    {
        ArgumentNullException.ThrowIfNull(roots);
        ArgumentNullException.ThrowIfNull(context);

        // A malformed range is the caller's mistake, but it is reported as a
        // resolution failure rather than thrown: the caller is usually a plan's
        // pinned range set months ago, and the dashboard should show which
        // constraint is unreadable rather than a 500.
        var requests = new List<RootRequest>(roots.Count);
        var failures = new List<FeaturePlanFailure>();

        foreach (var (slug, expression) in roots)
        {
            if (!FeatureSlug.IsValid(slug))
            {
                failures.Add(new FeaturePlanFailure(
                    nameof(ResolutionFailureCode.UnknownFeature), slug ?? string.Empty, $"'{slug}' is not a valid feature slug."));
                continue;
            }

            if (string.IsNullOrWhiteSpace(expression) || expression.Trim() is "*")
            {
                requests.Add(RootRequest.Latest(FeatureSlug.Normalize(slug)));
                continue;
            }

            if (!VersionRange.TryParse(expression, out var range))
            {
                failures.Add(new FeaturePlanFailure(
                    nameof(ResolutionFailureCode.NoMatchingVersion),
                    FeatureSlug.Normalize(slug),
                    $"'{expression}' is not a valid version range."));
                continue;
            }

            requests.Add(new RootRequest(FeatureSlug.Normalize(slug), range));
        }

        if (failures.Count > 0)
        {
            return new FeaturePlan([], failures);
        }

        var snapshot = await _versions.GetRegistrySnapshotAsync(cancellationToken);
        var resolver = new DependencyResolver(snapshot);

        var installed = new Dictionary<string, SemanticVersion>(StringComparer.Ordinal);
        foreach (var (slug, version) in context.InstalledFeatures)
        {
            // A store reporting a version KNIGHT cannot parse is a store whose
            // claim about that feature cannot be used. It is left out of the
            // context rather than guessed at, which makes the resolver treat the
            // feature as absent and plan a clean install.
            if (SemanticVersion.TryParse(version, out var parsed))
            {
                installed[slug] = parsed;
            }
        }

        var result = resolver.Resolve(
            requests,
            new StoreCompatibilityContext(
                context.StoreVersion,
                context.PythonVersion,
                context.DjangoVersion,
                context.HasDedicatedInfrastructure,
                installed,
                context.Database,
                context.Runtime,
                context.RuntimeVersion),
            moveForward);

        return new FeaturePlan(
            [.. result.Steps.Select(Translate)],
            [.. result.Failures.Select(failure =>
                new FeaturePlanFailure(failure.Code.ToString(), failure.Slug, failure.Message))]);
    }

    private static FeaturePlanStep Translate(PlanStep step) => new(
        step.FeatureId,
        step.VersionId,
        step.Slug,
        step.Name,
        step.Version.ToString(),
        step.InstalledVersion?.ToString(),
        step.Action switch
        {
            PlanAction.Install => FeaturePlanAction.Install,
            PlanAction.Upgrade => FeaturePlanAction.Upgrade,
            PlanAction.AlreadySatisfied => FeaturePlanAction.AlreadySatisfied,
            _ => FeaturePlanAction.DowngradeRefused,
        },
        step.IsRoot,
        step.RequiredBy,
        step.MigrationsRequired,
        step.MigrationsReversible,
        step.MigrationSeconds,
        step.RequiresRestart,
        step.IsExternalService);
}
