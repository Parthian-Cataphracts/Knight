using FeatureDelivery;
using FeatureRegistry.Domain;
using Knight.Application.Abstractions.ControlPlane;
using Knight.Infrastructure.ControlPlane;
using Knight.IntegrationTests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace Knight.IntegrationTests.ControlPlane;

/// <summary>
/// What a store is told about how to load a delivered package.
///
/// This exists because none of it was told at all. The install job carried the
/// artifact, the migration policy and the configuration, and nothing about which
/// Python module to put in INSTALLED_APPS or where to mount the Feature's URLs.
/// The store filled the gap by guessing the module name from the slug, which was
/// right only while the two happened to match — and once
/// <c>adr/0029</c> shortened every slug they stopped matching.
///
/// The failure was invisible from KNIGHT: publish succeeded, the job ran, every
/// step reported success, and a Feature that declared routes served none of them.
/// It was found by opening one in a browser in phase 13, which is the argument
/// for that being a release step rather than a courtesy
/// (docs/phase-13-verification.md).
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class DeliveryPayloadTests
{
    private readonly PostgresApiFixture _fixture;

    public DeliveryPayloadTests(PostgresApiFixture fixture)
    {
        _fixture = fixture;
    }

    private const string ManifestWithRoutes = """
        {
          "apiVersion": "knight.dev/v1",
          "slug": "reviews-ratings",
          "version": "1.0.0",
          "name": "Reviews and Ratings",
          "django": {
            "app_label": "knight_reviews",
            "installed_app": "knight_feature_reviews_ratings",
            "urls": { "include": "knight_feature_reviews_ratings.urls", "prefix": "reviews/" }
          },
          "compatibility": { "storeVersion": ">=1.0.0", "python": ">=3.12", "django": ">=5.0,<6.0" },
          "migrations": { "required": true, "reversible": true, "estimatedDurationSeconds": 5 },
          "install": { "strategy": "package-install", "healthCheck": "knight_feature_reviews_ratings.checks.health" },
          "uninstall": { "strategy": "disable-then-remove", "dataRetentionDays": 365 }
        }
        """;

    private const string ManifestWithoutRoutes = """
        {
          "apiVersion": "knight.dev/v1",
          "slug": "analytics-core",
          "version": "1.0.0",
          "name": "Analytics Core",
          "django": {
            "app_label": "knight_analytics_core",
            "installed_app": "knight_feature_analytics_core"
          },
          "compatibility": { "storeVersion": ">=1.0.0", "python": ">=3.12", "django": ">=5.0,<6.0" },
          "migrations": { "required": true, "reversible": true, "estimatedDurationSeconds": 5 },
          "install": { "strategy": "package-install", "healthCheck": "knight_feature_analytics_core.checks.health" },
          "uninstall": { "strategy": "disable-then-remove", "dataRetentionDays": 30 }
        }
        """;

    private async Task<DeliverableVersion?> DescribeAsync(string slug, string manifestJson)
    {
        using var scope = _fixture.Factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<ICustomerScopeAccessor>().SetPlatformScope();

        var context = scope.ServiceProvider.GetRequiredService<ControlPlaneDbContext>();

        // A slug per run: the catalogue is seeded once for the whole collection
        // and these tests must not collide with each other or with it.
        var uniqueSlug = $"{slug}-{Guid.NewGuid():n}"[..40].TrimEnd('-');
        var json = manifestJson.Replace($"\"{slug}\"", $"\"{uniqueSlug}\"");

        Assert.True(FeatureManifest.TryParse(json, out var manifest, out var errors), string.Join("; ", errors));

        var feature = Feature.Create(Guid.NewGuid(), DateTimeOffset.UtcNow, uniqueSlug, "Test feature", "Test");
        feature.Publish(DateTimeOffset.UtcNow);
        context.Features.Add(feature);

        var version = FeatureVersion.Create(
            Guid.NewGuid(),
            DateTimeOffset.UtcNow,
            feature.Id,
            manifest!,
            json,
            $"{uniqueSlug}-1.0.0.zip",
            new string('a', 64),
            1024,
            "signature",
            "dev",
            releaseNotes: null);

        context.FeatureVersions.Add(version);
        await context.SaveChangesAsync();

        var reader = scope.ServiceProvider.GetRequiredService<IFeatureVersionReader>();

        return await reader.GetForDeliveryAsync(version.Id, CancellationToken.None);
    }

    [Fact]
    public async Task TheModuleToLoadTravelsWithTheJobRatherThanBeingGuessedFromTheSlug()
    {
        if (!_fixture.IsAvailable) return;

        var deliverable = await DescribeAsync("reviews-ratings", ManifestWithRoutes);

        Assert.NotNull(deliverable);

        // The whole point: the slug is `reviews-ratings` and the module is not
        // `reviews_ratings`. A store deriving one from the other registers an
        // app it cannot import.
        Assert.Equal("knight_feature_reviews_ratings", deliverable!.Module);
        Assert.Equal("knight_reviews", deliverable.Namespace);
        Assert.NotEqual(deliverable.Slug.Replace("-", "_"), deliverable.Module);
    }

    [Fact]
    public async Task AFeatureThatDeclaresRoutesHasThemCarriedToTheStore()
    {
        if (!_fixture.IsAvailable) return;

        var deliverable = await DescribeAsync("reviews-ratings", ManifestWithRoutes);

        Assert.NotNull(deliverable);
        Assert.Equal("knight_feature_reviews_ratings.urls", deliverable!.MountExport);
        Assert.Equal("reviews/", deliverable.MountPrefix);
    }

    [Fact]
    public async Task AFeatureThatServesNoRoutesCarriesNone()
    {
        if (!_fixture.IsAvailable) return;

        var deliverable = await DescribeAsync("analytics-core", ManifestWithoutRoutes);

        Assert.NotNull(deliverable);

        // Null rather than a default prefix. The store mounts nothing for a
        // Feature with no urlconf, and inventing one would mount an import error.
        Assert.Null(deliverable!.MountExport);
        Assert.Null(deliverable.MountPrefix);
        Assert.Equal("knight_feature_analytics_core", deliverable.Module);
    }

    [Fact]
    public async Task TheWiringIsReadFromTheSignedManifestRatherThanAColumn()
    {
        if (!_fixture.IsAvailable) return;

        // The same argument the migration policy is read that way for: the
        // manifest is what the author signed, and a column beside it could drift
        // from it without anything noticing.
        var deliverable = await DescribeAsync("reviews-ratings", ManifestWithRoutes);

        Assert.NotNull(deliverable);
        Assert.True(deliverable!.MigrationsRequired);
        Assert.True(deliverable.MigrationsReversible);
        Assert.Equal(365, deliverable.DataRetentionDays);
        Assert.Equal("knight_reviews", deliverable.Namespace);
    }

    private const string ManifestWithExtensions = """
        {
          "apiVersion": "knight.dev/v1",
          "slug": "advanced-search",
          "version": "1.1.0",
          "name": "Advanced Search",
          "django": {
            "app_label": "knight_search",
            "installed_app": "knight_feature_advanced_search",
            "urls": { "include": "knight_feature_advanced_search.urls", "prefix": "search/" }
          },
          "compatibility": {
            "storeVersion": ">=1.0.0", "python": ">=3.12", "django": ">=5.0,<6.0", "database": "postgresql"
          },
          "migrations": {
            "required": true,
            "reversible": true,
            "estimatedDurationSeconds": 10,
            "extensions": ["pg_trgm"]
          },
          "install": { "strategy": "package-install", "healthCheck": "knight_feature_advanced_search.checks.health" },
          "uninstall": { "strategy": "disable-then-remove", "dataRetentionDays": 0 }
        }
        """;

    [Fact]
    public async Task ADeclaredExtensionTravelsToTheStoreWithTheJob()
    {
        if (!_fixture.IsAvailable) return;

        // The same lesson as the module name above, applied early rather than
        // after a phase of Features that did not work: a store cannot create an
        // extension it was never told about, and a Feature whose migration then
        // fails on a missing operator class reports a Postgres error naming
        // nothing an operator can act on (docs/adr/0031).
        var deliverable = await DescribeAsync("advanced-search", ManifestWithExtensions);

        Assert.NotNull(deliverable);
        Assert.Equal(["pg_trgm"], deliverable!.Extensions);

        // And it is still a reversible migration, which is the point of keeping
        // the extension out of it.
        Assert.True(deliverable.MigrationsReversible);
    }

    [Fact]
    public async Task AFeatureThatNeedsNoExtensionCarriesNone()
    {
        if (!_fixture.IsAvailable) return;

        var deliverable = await DescribeAsync("analytics-core", ManifestWithoutRoutes);

        Assert.NotNull(deliverable);
        Assert.Empty(deliverable!.Extensions);
    }
}
