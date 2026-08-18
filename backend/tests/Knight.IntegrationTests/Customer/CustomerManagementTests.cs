using System.Net;
using System.Net.Http.Json;
using Customer.Domain;
using Knight.Contracts.Common;
using Knight.Contracts.Customer;
using Knight.IntegrationTests.Catalog;
using Knight.IntegrationTests.Infrastructure;

namespace Knight.IntegrationTests.Customer;

[Collection(PostgresCollection.Name)]
public sealed class CustomerManagementTests
{
    private readonly PostgresApiFixture _fixture;

    public CustomerManagementTests(PostgresApiFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Customer_FullLifecycle_CreateReadUpdateArchiveRestore_Succeeds()
    {
        if (!_fixture.IsAvailable) return;

        var tenant = await _fixture.SeedCustomerTenantAsync(
            customerFeatureEnabled: true,
            permissions: PostgresApiFixture.AllCustomerPermissions());

        var client = CatalogTestClient.For(_fixture, tenant);

        // 1. Create customer
        var createRequest = new CreateCustomerRequest
        {
            DisplayName = "Jane Doe",
            Phone = "+1 (555) 234-5678",
            Email = "JANE.DOE@example.com"
        };

        var createResponse = await client.PostAsJsonAsync("/api/tenant/customers", createRequest);
        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);

        var created = await createResponse.Content.ReadFromJsonAsync<CustomerResponse>();
        Assert.NotNull(created);
        Assert.NotEqual(Guid.Empty, created.Id);
        Assert.Equal("Jane Doe", created.DisplayName);
        Assert.Equal("+1 (555) 234-5678", created.Phone);
        Assert.Equal("JANE.DOE@example.com", created.Email);
        Assert.Equal("Active", created.Status);

        // 2. Get customer by ID
        var getResponse = await client.GetAsync($"/api/tenant/customers/{created.Id}");
        Assert.Equal(HttpStatusCode.OK, getResponse.StatusCode);
        var fetched = await getResponse.Content.ReadFromJsonAsync<CustomerResponse>();
        Assert.NotNull(fetched);
        Assert.Equal(created.Id, fetched.Id);

        // 3. Update customer details
        var updateRequest = new UpdateCustomerRequest
        {
            DisplayName = "Jane Smith",
            Phone = "+15559876543",
            Email = "jane.smith@example.com"
        };

        var updateResponse = await client.PutAsJsonAsync($"/api/tenant/customers/{created.Id}", updateRequest);
        Assert.Equal(HttpStatusCode.OK, updateResponse.StatusCode);
        var updated = await updateResponse.Content.ReadFromJsonAsync<CustomerResponse>();
        Assert.NotNull(updated);
        Assert.Equal("Jane Smith", updated.DisplayName);
        Assert.Equal("+15559876543", updated.Phone);
        Assert.NotNull(updated.UpdatedAt);

        // 4. Archive customer
        var archiveResponse = await client.PostAsync($"/api/tenant/customers/{created.Id}/archive", null);
        Assert.Equal(HttpStatusCode.OK, archiveResponse.StatusCode);
        var archived = await archiveResponse.Content.ReadFromJsonAsync<CustomerResponse>();
        Assert.NotNull(archived);
        Assert.Equal("Archived", archived.Status);
        Assert.NotNull(archived.ArchivedAt);

        // 5. Attempting to archive already archived returns 409 Conflict
        var doubleArchiveResponse = await client.PostAsync($"/api/tenant/customers/{created.Id}/archive", null);
        Assert.Equal(HttpStatusCode.Conflict, doubleArchiveResponse.StatusCode);

        // 6. Restore customer
        var restoreResponse = await client.PostAsync($"/api/tenant/customers/{created.Id}/restore", null);
        Assert.Equal(HttpStatusCode.OK, restoreResponse.StatusCode);
        var restored = await restoreResponse.Content.ReadFromJsonAsync<CustomerResponse>();
        Assert.NotNull(restored);
        Assert.Equal("Active", restored.Status);
        Assert.Null(restored.ArchivedAt);

        // 7. Attempting to restore already active returns 409 Conflict
        var doubleRestoreResponse = await client.PostAsync($"/api/tenant/customers/{created.Id}/restore", null);
        Assert.Equal(HttpStatusCode.Conflict, doubleRestoreResponse.StatusCode);
    }

    [Fact]
    public async Task Customer_ListPaginationAndSearch_ReturnsCorrectResults()
    {
        if (!_fixture.IsAvailable) return;

        var tenant = await _fixture.SeedCustomerTenantAsync(
            customerFeatureEnabled: true,
            permissions: PostgresApiFixture.AllCustomerPermissions());

        var client = CatalogTestClient.For(_fixture, tenant);

        // Seed 3 customers
        await client.PostAsJsonAsync("/api/tenant/customers", new CreateCustomerRequest
        {
            DisplayName = "Alice Alpha",
            Phone = "+15551111111"
        });

        await client.PostAsJsonAsync("/api/tenant/customers", new CreateCustomerRequest
        {
            DisplayName = "Bob Beta",
            Email = "bob@example.com"
        });

        await client.PostAsJsonAsync("/api/tenant/customers", new CreateCustomerRequest
        {
            DisplayName = "Charlie Alpha",
            Phone = "+15552222222",
            Email = "charlie@example.com"
        });

        // Search for "Alpha"
        var searchResponse = await client.GetFromJsonAsync<PagedResponse<CustomerResponse>>("/api/tenant/customers?search=Alpha");
        Assert.NotNull(searchResponse);
        Assert.Equal(2, searchResponse.TotalCount);
        Assert.Contains(searchResponse.Items, c => c.DisplayName == "Alice Alpha");
        Assert.Contains(searchResponse.Items, c => c.DisplayName == "Charlie Alpha");
    }

    [Fact]
    public async Task SameContactValues_CanCoexistAcrossDifferentTenants()
    {
        if (!_fixture.IsAvailable) return;

        var tenantA = await _fixture.SeedCustomerTenantAsync(
            customerFeatureEnabled: true,
            permissions: PostgresApiFixture.AllCustomerPermissions());

        var tenantB = await _fixture.SeedCustomerTenantAsync(
            customerFeatureEnabled: true,
            permissions: PostgresApiFixture.AllCustomerPermissions());

        var clientA = CatalogTestClient.For(_fixture, tenantA);
        var clientB = CatalogTestClient.For(_fixture, tenantB);

        var sharedContact = new CreateCustomerRequest
        {
            DisplayName = "Shared Contact",
            Phone = "+15551234567",
            Email = "shared@family.org"
        };

        var responseA = await clientA.PostAsJsonAsync("/api/tenant/customers", sharedContact);
        Assert.Equal(HttpStatusCode.Created, responseA.StatusCode);

        var responseB = await clientB.PostAsJsonAsync("/api/tenant/customers", sharedContact);
        Assert.Equal(HttpStatusCode.Created, responseB.StatusCode);
    }
}
