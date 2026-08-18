using System.Net;
using System.Net.Http.Json;
using Catalog.Domain;
using Microsoft.EntityFrameworkCore;
using Knight.Contracts.Catalog;
using Knight.IntegrationTests.Infrastructure;
using Xunit;

namespace Knight.IntegrationTests.Catalog;

/// <summary>
/// Delete semantics across the catalog: a category (and a modifier group) is a
/// physical delete guarded by a conflict check, while a product is archived and a
/// variant deactivated so historical references stay resolvable.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class CatalogLifecycleTests
{
    private readonly PostgresApiFixture _fixture;

    public CatalogLifecycleTests(PostgresApiFixture fixture)
    {
        _fixture = fixture;
    }

    private Task<CatalogTenantContext> SeedTenantAsync() =>
        _fixture.SeedCatalogTenantAsync(true, PostgresApiFixture.AllCatalogPermissions());

    [Fact]
    public async Task DeletingACategoryThatStillHoldsProducts_Returns409_ThenSucceedsOnceEmptied()
    {
        if (!_fixture.IsAvailable) return;

        var tenant = await SeedTenantAsync();
        var categoryId = await _fixture.SeedCategoryAsync(tenant.TenantId, "Category A");
        var spareCategoryId = await _fixture.SeedCategoryAsync(tenant.TenantId, "Category B");
        var productId = await _fixture.SeedProductAsync(tenant.TenantId, categoryId, "Product A");
        var client = CatalogTestClient.For(_fixture, tenant);

        var conflict = await client.DeleteAsync($"/api/tenant/catalog/categories/{categoryId}");
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);

        // Reassign the product to another category, emptying the first one.
        var reassign = await client.PutAsJsonAsync(
            $"/api/tenant/catalog/products/{productId}/category",
            new ChangeProductCategoryRequest { CategoryId = spareCategoryId });
        Assert.Equal(HttpStatusCode.OK, reassign.StatusCode);

        var deleted = await client.DeleteAsync($"/api/tenant/catalog/categories/{categoryId}");
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);

        var stillExists = await _fixture.WithScopeAsync(
            (context, _) => context.Categories.AnyAsync(c => c.Id == categoryId),
            platformContext: true);

        Assert.False(stillExists);
    }

    [Fact]
    public async Task DeletingAModifierGroupWithProductAssignments_Returns409_ThenSucceedsOnceUnassigned()
    {
        if (!_fixture.IsAvailable) return;

        var tenant = await SeedTenantAsync();
        var categoryId = await _fixture.SeedCategoryAsync(tenant.TenantId, "Category A");
        var productId = await _fixture.SeedProductAsync(tenant.TenantId, categoryId, "Product A");
        var groupId = await _fixture.SeedModifierGroupAsync(tenant.TenantId, "Group A");
        var client = CatalogTestClient.For(_fixture, tenant);

        var assigned = await client.PutAsJsonAsync(
            $"/api/tenant/catalog/products/{productId}/modifier-groups",
            new ReplaceProductModifierGroupsRequest
            {
                Assignments = [new ProductModifierGroupAssignmentRequest { ModifierGroupId = groupId, DisplayOrder = 0 }]
            });
        Assert.Equal(HttpStatusCode.OK, assigned.StatusCode);

        var conflict = await client.DeleteAsync($"/api/tenant/catalog/modifier-groups/{groupId}");
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);

        // Replace-all with an empty set clears every assignment.
        var cleared = await client.PutAsJsonAsync(
            $"/api/tenant/catalog/products/{productId}/modifier-groups",
            new ReplaceProductModifierGroupsRequest { Assignments = [] });
        Assert.Equal(HttpStatusCode.OK, cleared.StatusCode);

        var deleted = await client.DeleteAsync($"/api/tenant/catalog/modifier-groups/{groupId}");
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
    }

    [Fact]
    public async Task ArchivingAProduct_KeepsItVisibleToAdminsAndRemovesItFromTheStorefront()
    {
        if (!_fixture.IsAvailable) return;

        var tenant = await SeedTenantAsync();
        var categoryId = await _fixture.SeedCategoryAsync(tenant.TenantId, "Category A");
        var productId = await _fixture.SeedProductAsync(
            tenant.TenantId, categoryId, "Product A", "product-a", ProductStatus.Active);

        var adminClient = CatalogTestClient.For(_fixture, tenant);
        var publicClient = CatalogTestClient.For(_fixture, tenant.Host);

        Assert.Equal(HttpStatusCode.OK, (await publicClient.GetAsync("/api/catalog/products/product-a")).StatusCode);

        var archived = await adminClient.DeleteAsync($"/api/tenant/catalog/products/{productId}");
        var archivedBody = await archived.Content.ReadFromJsonAsync<ProductResponse>();

        Assert.Equal(HttpStatusCode.OK, archived.StatusCode);
        Assert.Equal(nameof(ProductStatus.Archived), archivedBody!.Status);

        // Admins keep seeing everything, including archived rows.
        var adminGet = await adminClient.GetFromJsonAsync<ProductResponse>($"/api/tenant/catalog/products/{productId}");
        Assert.NotNull(adminGet);
        Assert.Equal(nameof(ProductStatus.Archived), adminGet!.Status);

        // The storefront no longer resolves it at all.
        Assert.Equal(HttpStatusCode.NotFound, (await publicClient.GetAsync("/api/catalog/products/product-a")).StatusCode);

        var rowStillPresent = await _fixture.WithScopeAsync(
            (context, _) => context.Products.AnyAsync(p => p.Id == productId),
            platformContext: true);

        Assert.True(rowStillPresent);
    }

    [Fact]
    public async Task ProductCreatedAsDraft_BecomesPublicOnlyAfterActivation()
    {
        if (!_fixture.IsAvailable) return;

        var tenant = await SeedTenantAsync();
        var categoryId = await _fixture.SeedCategoryAsync(tenant.TenantId, "Category A");
        var adminClient = CatalogTestClient.For(_fixture, tenant);
        var publicClient = CatalogTestClient.For(_fixture, tenant.Host);

        var created = await adminClient.PostAsJsonAsync("/api/tenant/catalog/products",
            new CreateProductRequest { CategoryId = categoryId, Name = "Product A", Slug = "product-a", BasePrice = 10m });
        var product = (await created.Content.ReadFromJsonAsync<ProductResponse>())!;

        Assert.Equal(nameof(ProductStatus.Draft), product.Status);
        Assert.Equal(HttpStatusCode.NotFound, (await publicClient.GetAsync("/api/catalog/products/product-a")).StatusCode);

        var activated = await adminClient.PostAsync($"/api/tenant/catalog/products/{product.Id}/activate", null);
        var activatedBody = (await activated.Content.ReadFromJsonAsync<ProductResponse>())!;

        Assert.Equal(HttpStatusCode.OK, activated.StatusCode);
        Assert.Equal(nameof(ProductStatus.Active), activatedBody.Status);
        Assert.Equal(HttpStatusCode.OK, (await publicClient.GetAsync("/api/catalog/products/product-a")).StatusCode);
    }

    [Fact]
    public async Task HidingAProduct_RemovesItFromTheStorefrontWithoutChangingItsStatus()
    {
        if (!_fixture.IsAvailable) return;

        var tenant = await SeedTenantAsync();
        var categoryId = await _fixture.SeedCategoryAsync(tenant.TenantId, "Category A");
        var productId = await _fixture.SeedProductAsync(
            tenant.TenantId, categoryId, "Product A", "product-a", ProductStatus.Active);

        var adminClient = CatalogTestClient.For(_fixture, tenant);
        var publicClient = CatalogTestClient.For(_fixture, tenant.Host);

        var hidden = await adminClient.PutAsJsonAsync(
            $"/api/tenant/catalog/products/{productId}/visibility",
            new SetVisibilityRequest { IsVisible = false });
        var hiddenBody = (await hidden.Content.ReadFromJsonAsync<ProductResponse>())!;

        Assert.Equal(HttpStatusCode.OK, hidden.StatusCode);
        Assert.False(hiddenBody.IsVisible);
        Assert.Equal(nameof(ProductStatus.Active), hiddenBody.Status);
        Assert.Equal(HttpStatusCode.NotFound, (await publicClient.GetAsync("/api/catalog/products/product-a")).StatusCode);
    }
}
