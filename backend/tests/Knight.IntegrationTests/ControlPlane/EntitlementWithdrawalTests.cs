using FeatureDelivery;
using FeatureDelivery.Domain;
using FeatureRegistry.Domain;
using Knight.Application.Abstractions.ControlPlane;
using Knight.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Knight.IntegrationTests.ControlPlane;

/// <summary>
/// What happens to installed code when the customer stops being entitled to it.
///
/// The policy is settled — losing an entitlement disables, it never uninstalls
/// and never deletes (docs/feature-delivery.md §11) — and until phase 20 nothing
/// tested it at all. What the delivery drill found is that it only applied to
/// installations in state <c>Installed</c>.
///
/// An installation whose last job failed is very often a store still running the
/// version it had before: an upgrade that fails at <c>verify</c> never touches
/// the working install. It stays in <c>Failed</c> until somebody looks at it,
/// and nobody looks at it at midnight, which is when subscriptions end. So a
/// customer who stopped paying kept the Feature — silently, for ever, on exactly
/// the stores whose last delivery had gone wrong.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class EntitlementWithdrawalTests
{
    private readonly PostgresApiFixture _fixture;

    public EntitlementWithdrawalTests(PostgresApiFixture fixture)
    {
        _fixture = fixture;
    }

    private static string Manifest(string slug) => $$"""
        {
          "apiVersion": "knight.dev/v1",
          "slug": "{{slug}}",
          "version": "1.0.0",
          "name": "{{slug}}",
          "django": { "app_label": "{{slug.Replace('-', '_')}}", "installed_app": "{{slug.Replace('-', '_')}}" },
          "compatibility": { "storeVersion": "*", "python": "*", "django": "*" },
          "migrations": { "required": true, "reversible": true, "estimatedDurationSeconds": 5 },
          "install": { "strategy": "package-install", "healthCheck": "{{slug.Replace('-', '_')}}.checks.health" },
          "uninstall": { "strategy": "disable-then-remove", "dataRetentionDays": 30 }
        }
        """;

    /// <summary>
    /// A store with one Feature on it, left in whichever state the test is about.
    ///
    /// Every state is reached through the real transitions rather than written
    /// down: an installation conjured straight into <c>Failed</c> would be a
    /// state the product cannot actually arrive at, and the whole question here
    /// is what the states people really reach mean.
    /// </summary>
    private async Task<(Guid CustomerId, Guid StoreId, Guid FeatureId)> SeedAsync(
        InstallationState leaveIn,
        bool everInstalled = true)
    {
        var customerId = await _fixture.SeedCustomerAsync();
        var storeId = await _fixture.SeedStoreAsync(customerId);
        await _fixture.SeedHeartbeatAsync(storeId);

        var slug = $"withdrawal-{Guid.NewGuid():n}"[..24];
        var featureId = Guid.NewGuid();

        await _fixture.WithControlPlaneScopeAsync(async (context, _) =>
        {
            var json = Manifest(slug);
            Assert.True(FeatureManifest.TryParse(json, out var manifest, out var errors), string.Join("; ", errors));

            var feature = Feature.Create(featureId, DateTimeOffset.UtcNow, slug, slug, "Test");
            feature.Publish(DateTimeOffset.UtcNow);
            context.Features.Add(feature);

            var version = FeatureVersion.Create(
                Guid.NewGuid(),
                DateTimeOffset.UtcNow,
                featureId,
                manifest!,
                json,
                $"{slug}-1.0.0.zip",
                new string('d', 64),
                1024,
                "signature",
                "dev",
                releaseNotes: null);

            version.Publish(Guid.NewGuid(), DateTimeOffset.UtcNow);
            context.FeatureVersions.Add(version);

            var installation = FeatureInstallation.Create(
                Guid.NewGuid(), DateTimeOffset.UtcNow, storeId, customerId, featureId, slug);

            if (everInstalled)
            {
                var install = Guid.NewGuid();
                installation.QueueJob(install, version.Id, "1.0.0", DateTimeOffset.UtcNow);
                installation.BeginWork(install, DateTimeOffset.UtcNow);
                installation.MarkInstalled(install, DateTimeOffset.UtcNow);
            }

            if (leaveIn is InstallationState.Failed)
            {
                // A second job that goes wrong. When the store was already
                // running 1.0.0 this is the upgrade-that-failed case: the row
                // says Failed and the store says 1.0.0, both truthfully.
                var second = Guid.NewGuid();
                installation.QueueJob(second, version.Id, "1.0.0", DateTimeOffset.UtcNow);
                installation.BeginWork(second, DateTimeOffset.UtcNow);
                installation.MarkFailed(
                    second,
                    "digest.mismatch",
                    "The artifact did not match its digest.",
                    RollbackOutcome.NotAttempted,
                    DateTimeOffset.UtcNow);
            }

            context.FeatureInstallations.Add(installation);
            await context.SaveChangesAsync();
        });

        return (customerId, storeId, featureId);
    }

    private async Task RevokeAsync(Guid customerId, Guid featureId)
    {
        using var scope = _fixture.Factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<ICustomerScopeAccessor>().SetPlatformScope();

        await scope.ServiceProvider.GetRequiredService<IFeatureDeliveryService>()
            .ApplyEntitlementChangeAsync(
                customerId,
                featureId,
                entitled: false,
                "subscription ended",
                CancellationToken.None);
    }

    private Task<List<JobType>> JobsForAsync(Guid storeId) =>
        _fixture.WithControlPlaneScopeAsync(async (context, _) =>
            await context.FeatureInstallationJobs
                .AsNoTracking()
                .Where(job => job.StoreId == storeId)
                .Select(job => job.Type)
                .ToListAsync());

    [Fact]
    public async Task LosingAnEntitlementDisablesAnInstalledFeature()
    {
        if (!_fixture.IsAvailable) return;

        var (customerId, storeId, featureId) = await SeedAsync(InstallationState.Installed);

        await RevokeAsync(customerId, featureId);

        Assert.Contains(JobType.Disable, await JobsForAsync(storeId));
    }

    [Fact]
    public async Task LosingAnEntitlementDisablesAFeatureWhoseLastJobFailed()
    {
        if (!_fixture.IsAvailable) return;

        var (customerId, storeId, featureId) = await SeedAsync(InstallationState.Failed);

        await RevokeAsync(customerId, featureId);

        // The defect this replaced: the code was on the store, enabled, serving
        // requests at the version the failed upgrade never replaced — and the
        // customer had stopped paying for it. Nothing disabled it, nothing said
        // so, and the only way to notice was to go looking.
        Assert.Contains(JobType.Disable, await JobsForAsync(storeId));
    }

    [Fact]
    public async Task AFirstInstallThatFailedHasNothingToDisable()
    {
        if (!_fixture.IsAvailable) return;

        var (customerId, storeId, featureId) = await SeedAsync(InstallationState.Failed, everInstalled: false);

        await RevokeAsync(customerId, featureId);

        // Failed, and never installed: there is no version of this on the store,
        // so there is nothing there to turn off. Queueing a Disable would send
        // an agent to disable something that was never there, and the job would
        // fail for a reason that reads like a real problem.
        Assert.DoesNotContain(JobType.Disable, await JobsForAsync(storeId));
    }
}
