using FeatureRegistry.Domain;
using Knight.Domain.Versioning;
using Xunit;

namespace Knight.UnitTests.ControlPlane;

/// <summary>
/// What a plan says carrying it out will cost the store.
///
/// These facts come from the Feature author's manifest and are read before
/// anything runs. One of them decides a safety gate: an irreversible migration
/// is the case the dashboard makes an operator type the store's name to confirm,
/// so a plan that reported it wrongly would either block a routine install or
/// wave through the one that cannot be undone
/// (docs/adr/0016-feature-migration-and-removal-policy.md).
/// </summary>
public sealed class PlanConsequenceTests
{
    private static FeatureManifest Manifest(
        string slug,
        string version,
        bool migrationsRequired,
        bool reversible,
        int seconds,
        bool requiresRestart)
    {
        var json = System.Text.Json.JsonSerializer.Serialize(new
        {
            apiVersion = FeatureManifest.SupportedApiVersion,
            slug,
            version,
            name = slug,
            django = new { app_label = slug.Replace('-', '_'), installed_app = slug.Replace('-', '_') },
            compatibility = new { storeVersion = "*", python = "*", django = "*" },
            migrations = new
            {
                required = migrationsRequired,
                reversible,
                estimatedDurationSeconds = seconds,
            },
            install = new { strategy = "package-install", requiresRestart },
        });

        Assert.True(FeatureManifest.TryParse(json, out var manifest, out var errors), string.Join("; ", errors));

        return manifest;
    }

    private static RegistryFeature Feature(
        string slug,
        bool migrationsRequired = false,
        bool reversible = true,
        int seconds = 0,
        bool requiresRestart = false) =>
        new(
            Guid.CreateVersion7(),
            slug,
            slug,
            FeatureStatus.Published,
            false,
            [
                new RegistryVersion(
                    Guid.CreateVersion7(),
                    SemanticVersion.Parse("1.0.0"),
                    Manifest(slug, "1.0.0", migrationsRequired, reversible, seconds, requiresRestart),
                    true),
            ]);

    private static StoreCompatibilityContext Store() =>
        new(
            "5.0.0",
            "3.12",
            "5.1",
            false,
            new Dictionary<string, SemanticVersion>(StringComparer.Ordinal),
            Database: null,
            Runtime: "django");

    [Fact]
    public void APlanStepCarriesTheMigrationFactsItsManifestDeclares()
    {
        var resolver = new DependencyResolver([
            Feature("analytics", migrationsRequired: true, reversible: false, seconds: 240, requiresRestart: true),
        ]);

        var step = Assert.Single(resolver.Resolve("analytics", VersionRange.Any, Store()).Steps);

        Assert.True(step.MigrationsRequired);
        Assert.False(step.MigrationsReversible);
        Assert.Equal(240, step.MigrationSeconds);
        Assert.True(step.RequiresRestart);
    }

    [Fact]
    public void AFeatureThatMigratesNothing_SaysSo()
    {
        var resolver = new DependencyResolver([Feature("storefront")]);

        var step = Assert.Single(resolver.Resolve("storefront", VersionRange.Any, Store()).Steps);

        Assert.False(step.MigrationsRequired);
        Assert.Equal(0, step.MigrationSeconds);
        Assert.False(step.RequiresRestart);

        // Reversible by default, which is the manifest's own default and the
        // safe reading: a Feature with no migrations has nothing to undo.
        Assert.True(step.MigrationsReversible);
    }

    /// <summary>A root that migrates nothing and depends on one that does.</summary>
    private static FeatureManifest DependsOnCore()
    {
        var json = System.Text.Json.JsonSerializer.Serialize(new
        {
            apiVersion = FeatureManifest.SupportedApiVersion,
            slug = "analytics",
            version = "1.0.0",
            name = "analytics",
            django = new { app_label = "analytics", installed_app = "analytics" },
            compatibility = new { storeVersion = "*", python = "*", django = "*" },
            dependencies = new { features = new[] { new { slug = "analytics-core", version = ">=1.0.0" } } },
            install = new { strategy = "package-install" },
        });

        Assert.True(FeatureManifest.TryParse(json, out var manifest, out var errors), string.Join("; ", errors));

        return manifest;
    }

    [Fact]
    public void TheFactsBelongToTheStepRatherThanToThePlan()
    {
        // A dependency that migrates and a root that does not. Reporting one
        // number for the run would hide which Feature is the irreversible one,
        // and that is the Feature somebody needs to be told about.
        var resolver = new DependencyResolver([
            Feature("analytics-core", migrationsRequired: true, reversible: false, seconds: 90),
            new RegistryFeature(
                Guid.CreateVersion7(),
                "analytics",
                "analytics",
                FeatureStatus.Published,
                false,
                [
                    new RegistryVersion(
                        Guid.CreateVersion7(),
                        SemanticVersion.Parse("1.0.0"),
                        DependsOnCore(),
                        true),
                ]),
        ]);

        var steps = resolver.Resolve("analytics", VersionRange.Any, Store()).Steps;

        var core = Assert.Single(steps, step => step.Slug == "analytics-core");
        var root = Assert.Single(steps, step => step.Slug == "analytics");

        Assert.False(core.MigrationsReversible);
        Assert.True(root.MigrationsReversible);
    }
}
