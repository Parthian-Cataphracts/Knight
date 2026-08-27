using FeatureDelivery;
using FeatureRegistry.Domain;
using Knight.Application.Abstractions.ControlPlane;
using Knight.Application.Exceptions;
using Knight.Infrastructure.ControlPlane;
using Knight.IntegrationTests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace Knight.IntegrationTests.ControlPlane;

/// <summary>
/// What an operator is told when a Feature cannot be installed.
///
/// Found in phase 18, by installing the whole catalogue into a real store
/// through the API instead of with the store's local install command. Every one
/// of the fifteen refusals came back as
/// <c>"No feature is registered with slug 'analytics-core'"</c> — about Features
/// sitting published in the catalogue, listed by the API two calls earlier.
///
/// The cause is worth keeping. A failed plan carries **no steps**, so there is no
/// root step to take a feature id from, and the code took a null id to mean a
/// missing Feature. The real reason — the store had never reported its Python,
/// Django or store version, so nothing could be checked for compatibility — was
/// in the plan the same request had just produced, and was thrown away.
///
/// Two things were wrong with that and both are tested here: the operator was
/// sent to look for a publishing problem that did not exist, and the blocking
/// reason that <c>docs/feature-delivery.md</c> §8 says a store's installation row
/// records was never recorded.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class BlockedInstallTests
{
    private readonly PostgresApiFixture _fixture;

    public BlockedInstallTests(PostgresApiFixture fixture)
    {
        _fixture = fixture;
    }

    /// <summary>A Feature that demands versions a silent store cannot satisfy.</summary>
    private static string Manifest(string slug) => $$"""
        {
          "apiVersion": "knight.dev/v1",
          "slug": "{{slug}}",
          "version": "1.0.0",
          "name": "{{slug}}",
          "runtime": "django",
          "django": { "app_label": "{{slug.Replace('-', '_')}}", "installed_app": "{{slug.Replace('-', '_')}}" },
          "compatibility": { "storeVersion": ">=1.0.0", "python": ">=3.12", "django": ">=5.0,<6.0" },
          "migrations": { "required": true, "reversible": true, "estimatedDurationSeconds": 5 },
          "install": { "strategy": "package-install", "healthCheck": "{{slug.Replace('-', '_')}}.checks.health" },
          "uninstall": { "strategy": "disable-then-remove", "dataRetentionDays": 30 }
        }
        """;

    /// <summary>
    /// A published Feature and a store that has never said what it runs.
    ///
    /// That is the state every store is in before its first heartbeat, which is
    /// exactly when an operator is most likely to try installing something.
    /// </summary>
    private async Task<(Guid StoreId, string Slug)> SeedPublishedFeatureAndSilentStoreAsync()
    {
        var customerId = await _fixture.SeedCustomerAsync();
        var storeId = await _fixture.SeedStoreAsync(customerId);
        var slug = $"blocked-{Guid.NewGuid():n}"[..20];

        using var scope = _fixture.Factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<ICustomerScopeAccessor>().SetPlatformScope();
        var context = scope.ServiceProvider.GetRequiredService<ControlPlaneDbContext>();

        var json = Manifest(slug);
        Assert.True(FeatureManifest.TryParse(json, out var manifest, out var errors), string.Join("; ", errors));

        var feature = Feature.Create(Guid.NewGuid(), DateTimeOffset.UtcNow, slug, slug, "Test");
        feature.Publish(DateTimeOffset.UtcNow);
        context.Features.Add(feature);

        var version = FeatureVersion.Create(
            Guid.NewGuid(),
            DateTimeOffset.UtcNow,
            feature.Id,
            manifest!,
            json,
            $"{slug}-1.0.0.zip",
            new string('c', 64),
            1024,
            "signature",
            "dev",
            releaseNotes: null);

        version.Publish(Guid.NewGuid(), DateTimeOffset.UtcNow);
        context.FeatureVersions.Add(version);

        await context.SaveChangesAsync();

        return (storeId, slug);
    }

    private async Task<T> ActAsync<T>(Func<IFeatureDeliveryService, Task<T>> act)
    {
        using var scope = _fixture.Factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<ICustomerScopeAccessor>().SetPlatformScope();

        return await act(scope.ServiceProvider.GetRequiredService<IFeatureDeliveryService>());
    }

    [Fact]
    public async Task APublishedFeatureThatCannotBeInstalledIsNotReportedAsMissing()
    {
        if (!_fixture.IsAvailable) return;

        var (storeId, slug) = await SeedPublishedFeatureAndSilentStoreAsync();

        var result = await ActAsync(service =>
            service.InstallAsync(new InstallFeatureInput(storeId, slug, null, null), CancellationToken.None));

        // A refusal with reasons, not a 404. The Feature is right there.
        Assert.False(result.Plan.IsSuccessful);
        Assert.Empty(result.QueuedJobs);
        Assert.NotEmpty(result.Plan.Failures);
    }

    [Fact]
    public async Task TheReasonsAreTheOnesThePlanFound()
    {
        if (!_fixture.IsAvailable) return;

        var (storeId, slug) = await SeedPublishedFeatureAndSilentStoreAsync();

        var result = await ActAsync(service =>
            service.InstallAsync(new InstallFeatureInput(storeId, slug, null, null), CancellationToken.None));

        // The store never reported what it runs, so nothing could be checked.
        // That is what an operator needs to read, and it is what `plan` had been
        // returning all along while `install` threw it away.
        Assert.All(result.Plan.Failures, failure => Assert.Equal("IncompatibleStore", failure.Code));
        Assert.Contains(result.Plan.Failures, failure => failure.Message.Contains("has not reported"));
    }

    [Fact]
    public async Task InstallAndPlanAgreeAboutWhyTheyRefused()
    {
        if (!_fixture.IsAvailable) return;

        var (storeId, slug) = await SeedPublishedFeatureAndSilentStoreAsync();

        var preview = await ActAsync(service =>
            service.PreviewAsync(storeId, slug, null, CancellationToken.None));

        var result = await ActAsync(service =>
            service.InstallAsync(new InstallFeatureInput(storeId, slug, null, null), CancellationToken.None));

        // The whole defect in one assertion: these two answered differently, and
        // the one an operator actually calls was the one that was wrong.
        Assert.Equal(
            preview.Failures.Select(failure => failure.Code).OrderBy(code => code),
            result.Plan.Failures.Select(failure => failure.Code).OrderBy(code => code));
    }

    [Fact]
    public async Task ASlugThatIsGenuinelyNotInTheCatalogueIsStillNotFound()
    {
        if (!_fixture.IsAvailable) return;

        var customerId = await _fixture.SeedCustomerAsync();
        var storeId = await _fixture.SeedStoreAsync(customerId);

        // The case the false 404 was borrowed from. It has to keep working, or
        // fixing the message would have swapped one wrong answer for another.
        await Assert.ThrowsAsync<NotFoundException>(() => ActAsync(service =>
            service.InstallAsync(
                new InstallFeatureInput(storeId, "no-such-feature-anywhere", null, null),
                CancellationToken.None)));
    }
}
