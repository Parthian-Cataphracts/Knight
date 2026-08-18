using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Knight.Contracts.Catalog;
using Knight.IntegrationTests.Infrastructure;
using Xunit;

namespace Knight.IntegrationTests.Catalog;

/// <summary>
/// "Exactly one default variant per product" and "exactly one primary image per
/// product" are enforced by a transactional swap in the repository plus a partial
/// unique index in PostgreSQL. Every assertion here reads the database back
/// directly rather than trusting the API response.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class CatalogInvariantTests
{
    private readonly PostgresApiFixture _fixture;

    public CatalogInvariantTests(PostgresApiFixture fixture)
    {
        _fixture = fixture;
    }

    private async Task<(CatalogTenantContext Tenant, Guid ProductId, HttpClient Client)> SeedProductAsync()
    {
        var tenant = await _fixture.SeedCatalogTenantAsync(true, PostgresApiFixture.AllCatalogPermissions());
        var categoryId = await _fixture.SeedCategoryAsync(tenant.TenantId, "Category A");
        var productId = await _fixture.SeedProductAsync(tenant.TenantId, categoryId, "Product A");

        return (tenant, productId, CatalogTestClient.For(_fixture, tenant));
    }

    private Task<List<Guid>> DefaultVariantIdsAsync(Guid productId) =>
        _fixture.WithScopeAsync(
            (context, _) => context.ProductVariants
                .AsNoTracking()
                .Where(v => v.ProductId == productId && v.IsDefault)
                .Select(v => v.Id)
                .ToListAsync(),
            platformContext: true);

    private Task<List<Guid>> PrimaryMediaIdsAsync(Guid productId) =>
        _fixture.WithScopeAsync(
            (context, _) => context.ProductMedia
                .AsNoTracking()
                .Where(m => m.ProductId == productId && m.IsPrimary)
                .Select(m => m.Id)
                .ToListAsync(),
            platformContext: true);

    private async Task AssertSoleDefaultAsync(Guid productId, Guid expectedVariantId)
    {
        var defaults = await DefaultVariantIdsAsync(productId);

        Assert.Single(defaults);
        Assert.Equal(expectedVariantId, defaults[0]);
    }

    private async Task AssertSolePrimaryAsync(Guid productId, Guid expectedMediaId)
    {
        var primaries = await PrimaryMediaIdsAsync(productId);

        Assert.Single(primaries);
        Assert.Equal(expectedMediaId, primaries[0]);
    }

    [Fact]
    public async Task FirstVariantCreatedForAProduct_BecomesTheDefault()
    {
        if (!_fixture.IsAvailable) return;

        var (_, productId, client) = await SeedProductAsync();

        var created = await client.PostAsJsonAsync($"/api/tenant/catalog/products/{productId}/variants",
            new CreateProductVariantRequest { Name = "Variant A", Price = 10m });
        var variant = await created.Content.ReadFromJsonAsync<ProductVariantResponse>();

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.NotNull(variant);
        Assert.True(variant!.IsDefault);
        await AssertSoleDefaultAsync(productId, variant.Id);
    }

    [Fact]
    public async Task SecondVariantCreated_DoesNotStealTheDefaultFlag()
    {
        if (!_fixture.IsAvailable) return;

        var (_, productId, client) = await SeedProductAsync();

        var firstResponse = await client.PostAsJsonAsync($"/api/tenant/catalog/products/{productId}/variants",
            new CreateProductVariantRequest { Name = "Variant A", Price = 10m });
        var first = (await firstResponse.Content.ReadFromJsonAsync<ProductVariantResponse>())!;

        var secondResponse = await client.PostAsJsonAsync($"/api/tenant/catalog/products/{productId}/variants",
            new CreateProductVariantRequest { Name = "Variant B", Price = 12m });
        var second = (await secondResponse.Content.ReadFromJsonAsync<ProductVariantResponse>())!;

        Assert.False(second.IsDefault);
        await AssertSoleDefaultAsync(productId, first.Id);
    }

    [Fact]
    public async Task SettingEachVariantDefaultInTurn_LeavesExactlyOneDefault()
    {
        if (!_fixture.IsAvailable) return;

        var (_, productId, client) = await SeedProductAsync();

        var first = (await (await client.PostAsJsonAsync($"/api/tenant/catalog/products/{productId}/variants",
            new CreateProductVariantRequest { Name = "Variant A", Price = 10m }))
            .Content.ReadFromJsonAsync<ProductVariantResponse>())!;
        var second = (await (await client.PostAsJsonAsync($"/api/tenant/catalog/products/{productId}/variants",
            new CreateProductVariantRequest { Name = "Variant B", Price = 12m }))
            .Content.ReadFromJsonAsync<ProductVariantResponse>())!;

        var promoteSecond = await client.PostAsync($"/api/tenant/catalog/products/{productId}/variants/{second.Id}/default", null);
        Assert.Equal(HttpStatusCode.NoContent, promoteSecond.StatusCode);
        await AssertSoleDefaultAsync(productId, second.Id);

        var promoteFirst = await client.PostAsync($"/api/tenant/catalog/products/{productId}/variants/{first.Id}/default", null);
        Assert.Equal(HttpStatusCode.NoContent, promoteFirst.StatusCode);
        await AssertSoleDefaultAsync(productId, first.Id);

        // And promoting the one that is already default is idempotent, not a
        // constraint violation.
        var promoteFirstAgain = await client.PostAsync($"/api/tenant/catalog/products/{productId}/variants/{first.Id}/default", null);
        Assert.Equal(HttpStatusCode.NoContent, promoteFirstAgain.StatusCode);
        await AssertSoleDefaultAsync(productId, first.Id);
    }

    [Fact]
    public async Task FirstMediaAddedForAProduct_BecomesPrimary()
    {
        if (!_fixture.IsAvailable) return;

        var (_, productId, client) = await SeedProductAsync();

        var created = await client.PostAsJsonAsync($"/api/tenant/catalog/products/{productId}/media",
            new AddProductMediaRequest { StorageKey = "products/alpha/photo-1.jpg" });
        var media = await created.Content.ReadFromJsonAsync<ProductMediaResponse>();

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.NotNull(media);
        Assert.True(media!.IsPrimary);
        await AssertSolePrimaryAsync(productId, media.Id);
    }

    [Fact]
    public async Task SettingEachMediaPrimaryInTurn_LeavesExactlyOnePrimary()
    {
        if (!_fixture.IsAvailable) return;

        var (_, productId, client) = await SeedProductAsync();

        var first = (await (await client.PostAsJsonAsync($"/api/tenant/catalog/products/{productId}/media",
            new AddProductMediaRequest { StorageKey = "products/alpha/photo-1.jpg" }))
            .Content.ReadFromJsonAsync<ProductMediaResponse>())!;
        var second = (await (await client.PostAsJsonAsync($"/api/tenant/catalog/products/{productId}/media",
            new AddProductMediaRequest { StorageKey = "products/alpha/photo-2.jpg", DisplayOrder = 1 }))
            .Content.ReadFromJsonAsync<ProductMediaResponse>())!;

        // Adding the second image must not have created a second primary row.
        await AssertSolePrimaryAsync(productId, first.Id);

        var promoteSecond = await client.PostAsync($"/api/tenant/catalog/products/{productId}/media/{second.Id}/primary", null);
        Assert.Equal(HttpStatusCode.NoContent, promoteSecond.StatusCode);
        await AssertSolePrimaryAsync(productId, second.Id);

        var promoteFirst = await client.PostAsync($"/api/tenant/catalog/products/{productId}/media/{first.Id}/primary", null);
        Assert.Equal(HttpStatusCode.NoContent, promoteFirst.StatusCode);
        await AssertSolePrimaryAsync(productId, first.Id);
    }

    [Fact]
    public async Task AddingMediaWithIsPrimaryRequested_AtomicallyDemotesThePrevious()
    {
        if (!_fixture.IsAvailable) return;

        var (_, productId, client) = await SeedProductAsync();

        await client.PostAsJsonAsync($"/api/tenant/catalog/products/{productId}/media",
            new AddProductMediaRequest { StorageKey = "products/alpha/photo-1.jpg" });

        var second = (await (await client.PostAsJsonAsync($"/api/tenant/catalog/products/{productId}/media",
            new AddProductMediaRequest { StorageKey = "products/alpha/photo-2.jpg", DisplayOrder = 1, IsPrimary = true }))
            .Content.ReadFromJsonAsync<ProductMediaResponse>())!;

        Assert.True(second.IsPrimary);
        await AssertSolePrimaryAsync(productId, second.Id);
    }

    [Fact]
    public async Task DeactivatingAVariant_KeepsTheRowAndOnlyClearsAvailability()
    {
        if (!_fixture.IsAvailable) return;

        var (_, productId, client) = await SeedProductAsync();

        var variant = (await (await client.PostAsJsonAsync($"/api/tenant/catalog/products/{productId}/variants",
            new CreateProductVariantRequest { Name = "Variant A", Price = 10m }))
            .Content.ReadFromJsonAsync<ProductVariantResponse>())!;

        var deleted = await client.DeleteAsync($"/api/tenant/catalog/products/{productId}/variants/{variant.Id}");
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);

        var persisted = await _fixture.WithScopeAsync(
            (context, _) => context.ProductVariants.AsNoTracking().FirstAsync(v => v.Id == variant.Id),
            platformContext: true);

        Assert.False(persisted.IsAvailable);
    }
}
