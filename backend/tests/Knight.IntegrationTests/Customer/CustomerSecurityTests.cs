using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Customer;
using Customer.Domain;
using Microsoft.EntityFrameworkCore;
using Knight.Contracts.Common;
using Knight.Contracts.Customer;
using Knight.IntegrationTests.Catalog;
using Knight.IntegrationTests.Infrastructure;

namespace Knight.IntegrationTests.Customer;

[Collection(PostgresCollection.Name)]
public sealed class CustomerSecurityTests
{
    private readonly PostgresApiFixture _fixture;

    public CustomerSecurityTests(PostgresApiFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task CrossTenant_ReadUpdateArchive_Returns404()
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

        // Tenant A creates customer
        var createResponse = await clientA.PostAsJsonAsync("/api/tenant/customers", new CreateCustomerRequest
        {
            DisplayName = "Tenant A Customer",
            Phone = "+15551111111"
        });
        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);
        var customerA = await createResponse.Content.ReadFromJsonAsync<CustomerResponse>();
        Assert.NotNull(customerA);

        // Tenant B attempts to read Customer A -> 404
        var readResponse = await clientB.GetAsync($"/api/tenant/customers/{customerA.Id}");
        Assert.Equal(HttpStatusCode.NotFound, readResponse.StatusCode);

        // Tenant B attempts to update Customer A -> 404
        var updateResponse = await clientB.PutAsJsonAsync($"/api/tenant/customers/{customerA.Id}", new UpdateCustomerRequest
        {
            DisplayName = "Hacked Name",
            Phone = "+15552222222"
        });
        Assert.Equal(HttpStatusCode.NotFound, updateResponse.StatusCode);

        // Tenant B attempts to archive Customer A -> 404
        var archiveResponse = await clientB.PostAsync($"/api/tenant/customers/{customerA.Id}/archive", null);
        Assert.Equal(HttpStatusCode.NotFound, archiveResponse.StatusCode);
    }

    [Fact]
    public async Task FeatureEnforcement_FeatureOff_PermissionOn_Returns403()
    {
        if (!_fixture.IsAvailable) return;

        var tenant = await _fixture.SeedCustomerTenantAsync(
            customerFeatureEnabled: false,
            permissions: PostgresApiFixture.AllCustomerPermissions());

        var client = CatalogTestClient.For(_fixture, tenant);

        var response = await client.GetAsync("/api/tenant/customers");
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task PermissionEnforcement_FeatureOn_PermissionOff_Returns403()
    {
        if (!_fixture.IsAvailable) return;

        var tenant = await _fixture.SeedCustomerTenantAsync(
            customerFeatureEnabled: true,
            permissions: []); // No permissions

        var client = CatalogTestClient.For(_fixture, tenant);

        var response = await client.GetAsync("/api/tenant/customers");
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task FeatureAndPermission_BothPresent_Returns200()
    {
        if (!_fixture.IsAvailable) return;

        var tenant = await _fixture.SeedCustomerTenantAsync(
            customerFeatureEnabled: true,
            permissions: [CustomerPermissions.CustomersView.Key]);

        var client = CatalogTestClient.For(_fixture, tenant);

        var response = await client.GetAsync("/api/tenant/customers");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task PlatformAdmin_CanInspectCustomers_EvenWhenFeatureIsDisabled()
    {
        if (!_fixture.IsAvailable) return;

        var tenant = await _fixture.SeedCustomerTenantAsync(
            customerFeatureEnabled: false,
            permissions: PostgresApiFixture.AllCustomerPermissions());

        // Create customer directly via platform scope
        var customerId = Guid.NewGuid();
        await _fixture.WithScopeAsync(async (context, _) =>
        {
            var c = global::Customer.Domain.Customer.Create(
                customerId,
                DateTimeOffset.UtcNow,
                tenant.TenantId,
                "Support Customer",
                "+15559998888",
                "support@test.org");

            await context.Customers.AddAsync(c);
            await context.SaveChangesAsync();
        }, platformContext: true);

        var platformToken = _fixture.CreatePlatformAdminToken();
        var platformClient = _fixture.Factory.CreateClient();
        platformClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", platformToken);

        var response = await platformClient.GetAsync($"/api/platform/tenants/{tenant.TenantId}/customers/{customerId}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var fetched = await response.Content.ReadFromJsonAsync<CustomerResponse>();
        Assert.NotNull(fetched);
        Assert.Equal("Support Customer", fetched.DisplayName);
    }

    [Fact]
    public async Task TenantUser_CannotAccess_PlatformCustomerEndpoints()
    {
        if (!_fixture.IsAvailable) return;

        var tenant = await _fixture.SeedCustomerTenantAsync(
            customerFeatureEnabled: true,
            permissions: PostgresApiFixture.AllCustomerPermissions());

        var tenantClient = _fixture.Factory.CreateClient();
        tenantClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tenant.Token);

        var response = await tenantClient.GetAsync($"/api/platform/tenants/{tenant.TenantId}/customers");
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task QueryFilter_FailsClosed_WhenNoTenantContext()
    {
        if (!_fixture.IsAvailable) return;

        var tenant = await _fixture.SeedCustomerTenantAsync(
            customerFeatureEnabled: true,
            permissions: PostgresApiFixture.AllCustomerPermissions());

        var customerId = Guid.NewGuid();
        await _fixture.WithScopeAsync(async (context, _) =>
        {
            var c = global::Customer.Domain.Customer.Create(
                customerId,
                DateTimeOffset.UtcNow,
                tenant.TenantId,
                "Hidden Customer",
                "+15550001111",
                null);

            await context.Customers.AddAsync(c);
            await context.SaveChangesAsync();
        }, platformContext: true);

        // Run query with anonymous/empty tenant context (platformContext: false, empty tenantId)
        await _fixture.WithScopeAsync(async (context, _) =>
        {
            var customers = await context.Customers.ToListAsync();
            Assert.Empty(customers);
        }, platformContext: false);
    }
}
