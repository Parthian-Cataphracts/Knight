using FeatureDelivery;
using FeatureDelivery.Domain;
using FeatureRegistry.Domain;
using Knight.Application.Abstractions.ControlPlane;
using Knight.Application.Exceptions;
using Knight.Infrastructure.ControlPlane;
using Knight.IntegrationTests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace Knight.IntegrationTests.ControlPlane;

/// <summary>
/// Refusing to uninstall a Feature something else depends on.
///
/// Carried out of phase 13 as a stated gap: the guard existed in
/// <c>FeatureDeliveryService</c> and nothing exercised it. The resolver half it
/// rests on was well covered — a dependency step is non-root and names who asked
/// for it — and the refusal itself was not, which is the half a customer meets.
///
/// The scenario is the real one from the catalogue: <c>customer-segmentation</c>
/// cannot compute a segment without <c>analytics-core</c>, so pulling analytics
/// out from under it must be refused rather than performed and discovered.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class UninstallGuardTests
{
    private readonly PostgresApiFixture _fixture;

    public UninstallGuardTests(PostgresApiFixture fixture)
    {
        _fixture = fixture;
    }

    private static string Manifest(string slug, string version, string? dependsOn = null)
    {
        var dependencies = dependsOn is null
            ? ""
            : $$"""
                "dependencies": { "features": [ { "slug": "{{dependsOn}}", "version": ">=1.0.0,<2.0.0" } ] },
                """;

        return $$"""
            {
              "apiVersion": "knight.dev/v1",
              "slug": "{{slug}}",
              "version": "{{version}}",
              "name": "{{slug}}",
              "django": { "app_label": "{{slug.Replace('-', '_')}}", "installed_app": "{{slug.Replace('-', '_')}}" },
              "compatibility": { "storeVersion": "*", "python": "*", "django": "*" },
              {{dependencies}}
              "migrations": { "required": true, "reversible": true, "estimatedDurationSeconds": 5 },
              "install": { "strategy": "package-install", "healthCheck": "{{slug.Replace('-', '_')}}.checks.health" },
              "uninstall": { "strategy": "disable-then-remove", "dataRetentionDays": 30 }
            }
            """;
    }

    /// <summary>
    /// A store with both Features installed, and the ids needed to act on them.
    /// </summary>
    private async Task<(Guid StoreId, Guid CoreId, Guid DependentId)> SeedInstalledPairAsync()
    {
        var customerId = await _fixture.SeedCustomerAsync();
        var storeId = await _fixture.SeedStoreAsync(customerId);

        // Unique per run: the catalogue is seeded once for the whole collection.
        var suffix = Guid.NewGuid().ToString("n")[..8];
        var coreSlug = $"core-{suffix}";
        var dependentSlug = $"dependent-{suffix}";

        using var scope = _fixture.Factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<ICustomerScopeAccessor>().SetPlatformScope();
        var context = scope.ServiceProvider.GetRequiredService<ControlPlaneDbContext>();

        var ids = new Dictionary<string, Guid>(StringComparer.Ordinal);

        foreach (var (slug, version, dependsOn) in new[]
                 {
                     (coreSlug, "1.1.0", (string?)null),
                     (dependentSlug, "1.0.0", coreSlug),
                 })
        {
            var json = Manifest(slug, version, dependsOn);
            Assert.True(FeatureManifest.TryParse(json, out var manifest, out var errors), string.Join("; ", errors));

            var feature = Feature.Create(Guid.NewGuid(), DateTimeOffset.UtcNow, slug, slug, "Test");
            feature.Publish(DateTimeOffset.UtcNow);
            context.Features.Add(feature);
            ids[slug] = feature.Id;

            var registryVersion = FeatureVersion.Create(
                Guid.NewGuid(),
                DateTimeOffset.UtcNow,
                feature.Id,
                manifest!,
                json,
                $"{slug}-{version}.zip",
                new string('b', 64),
                1024,
                "signature",
                "dev",
                releaseNotes: null);

            registryVersion.Publish(Guid.NewGuid(), DateTimeOffset.UtcNow);
            context.FeatureVersions.Add(registryVersion);

            // Recorded as installed and healthy, which is the only state from
            // which an uninstall is a sensible thing to ask for.
            var installation = FeatureInstallation.Create(
                Guid.NewGuid(), DateTimeOffset.UtcNow, storeId, customerId, feature.Id, slug);

            // Through the real state machine rather than around it:
            // NotInstalled -> Pending -> Installing -> Installed. An installation
            // conjured straight into Installed would be a state the product
            // cannot actually reach.
            var jobId = Guid.NewGuid();
            installation.QueueJob(jobId, registryVersion.Id, version, DateTimeOffset.UtcNow);
            installation.BeginWork(jobId, DateTimeOffset.UtcNow);
            installation.MarkInstalled(jobId, DateTimeOffset.UtcNow);

            context.FeatureInstallations.Add(installation);
        }

        await context.SaveChangesAsync();

        return (storeId, ids[coreSlug], ids[dependentSlug]);
    }

    private async Task<T> ActAsync<T>(Func<IFeatureDeliveryService, Task<T>> act)
    {
        using var scope = _fixture.Factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<ICustomerScopeAccessor>().SetPlatformScope();

        return await act(scope.ServiceProvider.GetRequiredService<IFeatureDeliveryService>());
    }

    [Fact]
    public async Task ADependencyCannotBeUninstalledWhileSomethingNeedsIt()
    {
        if (!_fixture.IsAvailable) return;

        var (storeId, coreId, _) = await SeedInstalledPairAsync();

        var refused = await Assert.ThrowsAsync<ConflictException>(
            () => ActAsync(service => service.UninstallAsync(storeId, coreId, CancellationToken.None)));

        // The message has to name what is in the way. "Cannot uninstall" on its
        // own leaves an operator guessing which Feature to remove first.
        Assert.Contains("depends on it", refused.Message);
        Assert.Contains("dependent-", refused.Message);
    }

    [Fact]
    public async Task TheDependentItselfCanBeUninstalled()
    {
        if (!_fixture.IsAvailable) return;

        // The other half of the guard, and the half that proves it is a guard
        // rather than a blanket refusal.
        var (storeId, _, dependentId) = await SeedInstalledPairAsync();

        var job = await ActAsync(
            service => service.UninstallAsync(storeId, dependentId, CancellationToken.None));

        Assert.NotNull(job);
        Assert.Equal(JobType.Uninstall, job.Type);
    }

    [Fact]
    public async Task OnceTheDependentIsGoneTheDependencyCanFollow()
    {
        if (!_fixture.IsAvailable) return;

        // "Uninstall the dependent features first" has to actually work, or the
        // refusal is advice a customer cannot act on.
        var (storeId, coreId, dependentId) = await SeedInstalledPairAsync();

        var dependentJob = await ActAsync(
            service => service.UninstallAsync(storeId, dependentId, CancellationToken.None));

        using (var scope = _fixture.Factory.Services.CreateScope())
        {
            scope.ServiceProvider.GetRequiredService<ICustomerScopeAccessor>().SetPlatformScope();
            var context = scope.ServiceProvider.GetRequiredService<ControlPlaneDbContext>();

            var installation = context.FeatureInstallations.Single(
                item => item.StoreId == storeId && item.FeatureId == dependentId);

            installation.MarkUninstalled(dependentJob.Id, dataRetentionDays: 30, DateTimeOffset.UtcNow);

            // The job has to finish too. A store runs one installation job at a
            // time, so leaving this one in flight would make the second
            // uninstall fail for that reason rather than for the guard - and the
            // test would pass while proving nothing.
            var finished = context.FeatureInstallationJobs.Single(item => item.Id == dependentJob.Id);
            finished.Claim(DateTimeOffset.UtcNow, TimeSpan.FromMinutes(5));
            finished.Succeed(DateTimeOffset.UtcNow);

            await context.SaveChangesAsync();
        }

        var job = await ActAsync(
            service => service.UninstallAsync(storeId, coreId, CancellationToken.None));

        Assert.Equal(JobType.Uninstall, job.Type);
    }
}
