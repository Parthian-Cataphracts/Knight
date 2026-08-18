using Catalog;
using Microsoft.Extensions.DependencyInjection;
using Knight.Application.Authorization;
using Knight.IntegrationTests.Infrastructure;
using Xunit;

namespace Knight.IntegrationTests.Catalog;

/// <summary>
/// Module registration must be self-service: adding the Catalog module to the
/// host is enough for its permissions to appear in the shared catalog, with no
/// edit to Identity or to any central permission list. This is checked against
/// the real composed host rather than a hand-built container.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class CatalogPermissionRegistrationTests
{
    private readonly PostgresApiFixture _fixture;

    public CatalogPermissionRegistrationTests(PostgresApiFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task AllTwelveCatalogPermissions_AreRegisteredInTheSharedCatalog()
    {
        if (!_fixture.IsAvailable) return;

        var registeredKeys = await _fixture.WithScopeAsync(
            (_, sp) => Task.FromResult(
                sp.GetRequiredService<IPermissionCatalog>().All.Select(p => p.Key).ToHashSet(StringComparer.Ordinal)),
            platformContext: true);

        Assert.Equal(12, CatalogPermissions.All.Count);

        foreach (var permission in CatalogPermissions.All)
        {
            Assert.Contains(permission.Key, registeredKeys);
        }
    }

    [Fact]
    public async Task EveryCatalogPermission_DeclaresTheCatalogModule()
    {
        if (!_fixture.IsAvailable) return;

        var registered = await _fixture.WithScopeAsync(
            (_, sp) => Task.FromResult(sp.GetRequiredService<IPermissionCatalog>().All.ToArray()),
            platformContext: true);

        var catalogPermissions = registered.Where(p => p.Key.StartsWith("catalog.", StringComparison.Ordinal)).ToArray();

        Assert.Equal(12, catalogPermissions.Length);
        Assert.All(catalogPermissions, p => Assert.Equal("catalog", p.Module));
    }

    [Fact]
    public async Task CatalogPermissions_AreRecognizedByTheCatalogRegistrationCheck()
    {
        if (!_fixture.IsAvailable) return;

        var allRegistered = await _fixture.WithScopeAsync(
            (_, sp) =>
            {
                var catalog = sp.GetRequiredService<IPermissionCatalog>();
                return Task.FromResult(CatalogPermissions.All.All(catalog.IsRegistered));
            },
            platformContext: true);

        Assert.True(allRegistered);
    }
}
