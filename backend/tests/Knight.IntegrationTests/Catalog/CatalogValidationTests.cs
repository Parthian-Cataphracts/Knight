using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Knight.Contracts.Catalog;
using Knight.IntegrationTests.Infrastructure;
using Xunit;

namespace Knight.IntegrationTests.Catalog;

/// <summary>
/// Domain validation must survive the trip through the API: an invalid selection
/// rule or a negative price is rejected at the HTTP boundary, not silently
/// persisted. Prices are also round-tripped through PostgreSQL to prove the
/// <c>numeric(18,2)</c> mapping preserves the exact decimal value.
///
/// Note on the status code: every rejection below is a malformed input — a negative
/// price, a self-contradictory selection rule, a storage key shaped like a filesystem
/// path — and is invalid regardless of what is already stored. Those carry
/// <c>DomainErrorCategory.Validation</c>, which <c>ExceptionHandlingMiddleware</c>
/// maps to HTTP 400. A 409 is reserved for collisions with existing state (a duplicate
/// slug or SKU, deleting a category that still holds products), covered separately in
/// <see cref="CatalogUniquenessTests"/> and <see cref="CatalogLifecycleTests"/>.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class CatalogValidationTests
{
    private readonly PostgresApiFixture _fixture;

    public CatalogValidationTests(PostgresApiFixture fixture)
    {
        _fixture = fixture;
    }

    private Task<CatalogTenantContext> SeedTenantAsync() =>
        _fixture.SeedCatalogTenantAsync(true, PostgresApiFixture.AllCatalogPermissions());

    private static void AssertRejected(HttpResponseMessage response) =>
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

    [Fact]
    public async Task ModifierGroupWithNegativeMinimumSelections_IsRejected()
    {
        if (!_fixture.IsAvailable) return;

        var tenant = await SeedTenantAsync();
        var client = CatalogTestClient.For(_fixture, tenant);

        var response = await client.PostAsJsonAsync("/api/tenant/catalog/modifier-groups",
            new CreateModifierGroupRequest { Name = "Group A", IsRequired = false, MinSelections = -1, MaxSelections = 3 });

        AssertRejected(response);
    }

    [Fact]
    public async Task ModifierGroupWithMaximumBelowMinimum_IsRejected()
    {
        if (!_fixture.IsAvailable) return;

        var tenant = await SeedTenantAsync();
        var client = CatalogTestClient.For(_fixture, tenant);

        var response = await client.PostAsJsonAsync("/api/tenant/catalog/modifier-groups",
            new CreateModifierGroupRequest { Name = "Group A", IsRequired = false, MinSelections = 2, MaxSelections = 1 });

        AssertRejected(response);
    }

    [Fact]
    public async Task RequiredModifierGroupWithZeroMinimumSelections_IsRejected()
    {
        if (!_fixture.IsAvailable) return;

        var tenant = await SeedTenantAsync();
        var client = CatalogTestClient.For(_fixture, tenant);

        var response = await client.PostAsJsonAsync("/api/tenant/catalog/modifier-groups",
            new CreateModifierGroupRequest { Name = "Group A", IsRequired = true, MinSelections = 0, MaxSelections = 3 });

        AssertRejected(response);
    }

    [Fact]
    public async Task ValidRequiredModifierGroupConfiguration_Succeeds()
    {
        if (!_fixture.IsAvailable) return;

        var tenant = await SeedTenantAsync();
        var client = CatalogTestClient.For(_fixture, tenant);

        var response = await client.PostAsJsonAsync("/api/tenant/catalog/modifier-groups",
            new CreateModifierGroupRequest { Name = "Group A", IsRequired = true, MinSelections = 1, MaxSelections = 3 });
        var group = await response.Content.ReadFromJsonAsync<ModifierGroupResponse>();

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.NotNull(group);
        Assert.True(group!.IsRequired);
        Assert.Equal(1, group.MinSelections);
        Assert.Equal(3, group.MaxSelections);
    }

    [Fact]
    public async Task ProductWithNegativeBasePrice_IsRejected()
    {
        if (!_fixture.IsAvailable) return;

        var tenant = await SeedTenantAsync();
        var categoryId = await _fixture.SeedCategoryAsync(tenant.TenantId, "Category A");
        var client = CatalogTestClient.For(_fixture, tenant);

        var response = await client.PostAsJsonAsync("/api/tenant/catalog/products",
            new CreateProductRequest { CategoryId = categoryId, Name = "Product A", BasePrice = -1m });

        AssertRejected(response);

        var persisted = await _fixture.WithScopeAsync(
            (context, _) => context.Products.AnyAsync(p => p.TenantId == tenant.TenantId),
            platformContext: true);

        Assert.False(persisted);
    }

    [Fact]
    public async Task VariantWithNegativePrice_IsRejected()
    {
        if (!_fixture.IsAvailable) return;

        var tenant = await SeedTenantAsync();
        var categoryId = await _fixture.SeedCategoryAsync(tenant.TenantId, "Category A");
        var productId = await _fixture.SeedProductAsync(tenant.TenantId, categoryId, "Product A");
        var client = CatalogTestClient.For(_fixture, tenant);

        var response = await client.PostAsJsonAsync($"/api/tenant/catalog/products/{productId}/variants",
            new CreateProductVariantRequest { Name = "Variant A", Price = -1m });

        AssertRejected(response);
    }

    [Fact]
    public async Task VariantWithNegativeCompareAtPrice_IsRejected()
    {
        if (!_fixture.IsAvailable) return;

        var tenant = await SeedTenantAsync();
        var categoryId = await _fixture.SeedCategoryAsync(tenant.TenantId, "Category A");
        var productId = await _fixture.SeedProductAsync(tenant.TenantId, categoryId, "Product A");
        var client = CatalogTestClient.For(_fixture, tenant);

        var response = await client.PostAsJsonAsync($"/api/tenant/catalog/products/{productId}/variants",
            new CreateProductVariantRequest { Name = "Variant A", Price = 10m, CompareAtPrice = -1m });

        AssertRejected(response);
    }

    [Fact]
    public async Task ModifierWithNegativePriceDelta_IsRejected()
    {
        if (!_fixture.IsAvailable) return;

        var tenant = await SeedTenantAsync();
        var groupId = await _fixture.SeedModifierGroupAsync(tenant.TenantId, "Group A");
        var client = CatalogTestClient.For(_fixture, tenant);

        var response = await client.PostAsJsonAsync($"/api/tenant/catalog/modifier-groups/{groupId}/modifiers",
            new CreateModifierRequest { Name = "Modifier A", PriceDelta = -1m });

        AssertRejected(response);
    }

    [Fact]
    public async Task ProductMediaWithPathLikeStorageKey_IsRejected()
    {
        if (!_fixture.IsAvailable) return;

        var tenant = await SeedTenantAsync();
        var categoryId = await _fixture.SeedCategoryAsync(tenant.TenantId, "Category A");
        var productId = await _fixture.SeedProductAsync(tenant.TenantId, categoryId, "Product A");
        var client = CatalogTestClient.For(_fixture, tenant);

        var response = await client.PostAsJsonAsync($"/api/tenant/catalog/products/{productId}/media",
            new AddProductMediaRequest { StorageKey = "../../etc/passwd" });

        AssertRejected(response);
    }

    /// <summary>
    /// The money columns are <c>numeric(18,2)</c>, not a floating-point type, so a
    /// value written through the API must come back byte-for-byte identical rather
    /// than approximated.
    /// </summary>
    [Fact]
    public async Task PreciseDecimalPrices_RoundTripExactlyThroughPostgres()
    {
        if (!_fixture.IsAvailable) return;

        var tenant = await SeedTenantAsync();
        var categoryId = await _fixture.SeedCategoryAsync(tenant.TenantId, "Category A");
        var client = CatalogTestClient.For(_fixture, tenant);

        var created = await client.PostAsJsonAsync("/api/tenant/catalog/products",
            new CreateProductRequest { CategoryId = categoryId, Name = "Product A", BasePrice = 19.99m });
        var product = (await created.Content.ReadFromJsonAsync<ProductResponse>())!;

        Assert.Equal(19.99m, product.BasePrice);

        var reread = await client.GetFromJsonAsync<ProductResponse>($"/api/tenant/catalog/products/{product.Id}");
        Assert.NotNull(reread);
        Assert.Equal(19.99m, reread!.BasePrice);

        var fromDatabase = await _fixture.WithScopeAsync(
            (context, _) => context.Products.AsNoTracking().Where(p => p.Id == product.Id).Select(p => p.BasePrice).FirstAsync(),
            platformContext: true);

        Assert.Equal(19.99m, fromDatabase);

        var variantResponse = await client.PostAsJsonAsync($"/api/tenant/catalog/products/{product.Id}/variants",
            new CreateProductVariantRequest { Name = "Variant A", Price = 1234.56m, CompareAtPrice = 1500.05m });
        var variant = (await variantResponse.Content.ReadFromJsonAsync<ProductVariantResponse>())!;

        Assert.Equal(1234.56m, variant.Price);
        Assert.Equal(1500.05m, variant.CompareAtPrice);

        var variantFromDatabase = await _fixture.WithScopeAsync(
            (context, _) => context.ProductVariants.AsNoTracking().FirstAsync(v => v.Id == variant.Id),
            platformContext: true);

        Assert.Equal(1234.56m, variantFromDatabase.Price);
        Assert.Equal(1500.05m, variantFromDatabase.CompareAtPrice);
    }
}
