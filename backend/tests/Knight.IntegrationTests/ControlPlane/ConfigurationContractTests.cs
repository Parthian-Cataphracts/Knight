using AccessControl.Domain;
using FeatureDelivery;
using FeatureDelivery.Domain;
using FeatureRegistry.Domain;
using Knight.Application.Abstractions.ControlPlane;
using Knight.Application.Exceptions;
using Knight.Infrastructure.ControlPlane;
using Knight.IntegrationTests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Stores.Domain;
using Xunit;

namespace Knight.IntegrationTests.ControlPlane;

/// <summary>
/// A configuration is judged against the Feature's own manifest.
///
/// The failure this closes is silent and ordinary: an operator sets
/// `retry_attemps`, sees it saved, and waits for behaviour that never changes
/// because nothing reads that key. The same for a secret — an unrecognised one
/// is a credential encrypted, stored and never used, which is a liability with
/// no upside.
///
/// Judged against the manifest of the version the store has, because that is the
/// document the Feature's author signed. Carried from phase 3.5, and it matters
/// more since phase 22: for an external Feature the configuration *is* what was
/// delivered.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class ConfigurationContractTests
{
    private readonly PostgresApiFixture _fixture;

    public ConfigurationContractTests(PostgresApiFixture fixture)
    {
        _fixture = fixture;
    }

    private static string Manifest(string slug) => $$"""
        {
          "apiVersion": "knight.dev/v1",
          "slug": "{{slug}}",
          "version": "1.0.0",
          "name": "{{slug}}",
          "runtime": "django",
          "django": { "app_label": "{{slug.Replace('-', '_')}}", "installed_app": "{{slug.Replace('-', '_')}}" },
          "compatibility": { "storeVersion": ">=1.0.0", "python": ">=3.12", "django": ">=5.0,<6.0" },
          "migrations": { "required": false, "reversible": true, "estimatedDurationSeconds": 1 },
          "install": { "strategy": "package-install", "healthCheck": "{{slug.Replace('-', '_')}}.checks.health" },
          "uninstall": { "strategy": "disable-then-remove", "dataRetentionDays": 30 },
          "configuration": {
            "defaults": { "retry_attempts": 3, "sender_name": "The shop", "digest": true },
            "secrets": ["PROVIDER_API_KEY"]
          }
        }
        """;

    [Fact]
    public async Task A_declared_setting_is_accepted()
    {
        var (storeId, featureId) = await SeedAsync();

        var job = await ConfigureAsync(storeId, featureId, """{"retry_attempts": 5}""");

        Assert.Equal(JobType.ApplyConfiguration, job.Type);
    }

    [Fact]
    public async Task A_setting_the_manifest_never_declared_is_refused()
    {
        var (storeId, featureId) = await SeedAsync();

        var refusal = await Assert.ThrowsAsync<ValidationException>(
            () => ConfigureAsync(storeId, featureId, """{"retry_attemps": 5}"""));

        // The typo, and what it should have been. Saving it and doing nothing is
        // what this replaces.
        Assert.Contains("values.retry_attemps", refusal.Errors.Keys);
        Assert.Contains("retry_attempts", string.Join(" ", refusal.Errors["values.retry_attemps"]));
    }

    [Fact]
    public async Task A_setting_of_the_wrong_type_is_refused()
    {
        var (storeId, featureId) = await SeedAsync();

        var refusal = await Assert.ThrowsAsync<ValidationException>(
            () => ConfigureAsync(storeId, featureId, """{"retry_attempts": "five"}"""));

        Assert.Contains("values.retry_attempts", refusal.Errors.Keys);
    }

    [Fact]
    public async Task Null_clears_a_setting_rather_than_being_a_type_error()
    {
        var (storeId, featureId) = await SeedAsync();

        // How a caller says "use the default". Refusing it would make clearing a
        // setting impossible.
        await ConfigureAsync(storeId, featureId, """{"sender_name": null}""");
    }

    [Fact]
    public async Task A_secret_the_manifest_never_declared_is_refused()
    {
        var (storeId, featureId) = await SeedAsync();

        var refusal = await Assert.ThrowsAsync<ValidationException>(
            () => ConfigureAsync(
                storeId,
                featureId,
                "{}",
                new Dictionary<string, string> { ["SOME_OTHER_KEY"] = "a-value" }));

        // A credential encrypted, stored and never read is a liability with no
        // upside.
        Assert.Contains("secrets.SOME_OTHER_KEY", refusal.Errors.Keys);
    }

    [Fact]
    public async Task A_declared_secret_is_accepted()
    {
        var (storeId, featureId) = await SeedAsync();

        await ConfigureAsync(
            storeId,
            featureId,
            "{}",
            new Dictionary<string, string> { ["PROVIDER_API_KEY"] = "a-value" });
    }

    // --- Helpers ---------------------------------------------------------------

    private async Task<FeatureInstallationJob> ConfigureAsync(
        Guid storeId,
        Guid featureId,
        string values,
        IReadOnlyDictionary<string, string>? secrets = null)
    {
        using var scope = _fixture.Factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<ICustomerScopeAccessor>().SetPlatformScope();

        return await scope.ServiceProvider.GetRequiredService<IFeatureDeliveryService>().ConfigureAsync(
            storeId,
            featureId,
            values,
            secrets ?? new Dictionary<string, string>(),
            CancellationToken.None);
    }

    /// <summary>A published Feature, installed on a store, with a manifest that declares three settings and one secret.</summary>
    private async Task<(Guid StoreId, Guid FeatureId)> SeedAsync()
    {
        var customerId = await _fixture.SeedCustomerAsync();
        var storeId = await _fixture.SeedStoreAsync(customerId, StoreEnvironment.Production);
        var slug = $"cfg-{Guid.NewGuid():n}"[..16];

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

        var installation = FeatureInstallation.Create(
            Guid.NewGuid(), DateTimeOffset.UtcNow, storeId, customerId, feature.Id, slug);

        var jobId = Guid.NewGuid();
        installation.QueueJob(jobId, version.Id, "1.0.0", DateTimeOffset.UtcNow);
        installation.BeginWork(jobId, DateTimeOffset.UtcNow);
        installation.MarkInstalled(jobId, DateTimeOffset.UtcNow);

        context.FeatureInstallations.Add(installation);

        await context.SaveChangesAsync();

        return (storeId, feature.Id);
    }
}
