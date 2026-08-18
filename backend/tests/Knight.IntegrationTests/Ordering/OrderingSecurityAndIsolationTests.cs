using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Ordering;
using Ordering.Domain;
using Knight.Contracts.Common;
using Knight.Contracts.Ordering;
using Knight.IntegrationTests.Catalog;
using Knight.IntegrationTests.Infrastructure;
using Xunit;

namespace Knight.IntegrationTests.Ordering;

[Collection(PostgresCollection.Name)]
public sealed class OrderingSecurityAndIsolationTests
{
    private readonly PostgresApiFixture _fixture;

    public OrderingSecurityAndIsolationTests(PostgresApiFixture fixture)
    {
        _fixture = fixture;
    }

    private async Task<Guid> SeedOrderAsync(Guid tenantId)
    {
        var categoryId = await _fixture.SeedCategoryAsync(tenantId, "Category " + Guid.NewGuid());
        var productId = await _fixture.SeedProductAsync(tenantId, categoryId, "Product " + Guid.NewGuid(), basePrice: 15.00m);

        PlaceOrderResult result = null!;
        await _fixture.WithScopeAsync(async (_, sp) =>
        {
            var placementService = sp.GetRequiredService<IOrderPlacementService>();
            result = await placementService.PlaceOrderAsync(
                tenantId,
                new PlaceOrderInput([new PlaceOrderItemInput(productId, null, 1)]),
                null,
                CancellationToken.None);
        }, platformContext: true);

        return result.OrderId;
    }

    [Fact]
    public async Task FeatureEnforcement_FeatureOff_PermissionOn_Returns403()
    {
        if (!_fixture.IsAvailable) return;

        var tenant = await _fixture.SeedOrderingTenantAsync(
            orderingFeatureEnabled: false,
            permissions: PostgresApiFixture.AllOrderingPermissions());

        var client = CatalogTestClient.For(_fixture, tenant);
        var response = await client.GetAsync("/api/tenant/orders");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task PermissionEnforcement_FeatureOn_PermissionOff_Returns403()
    {
        if (!_fixture.IsAvailable) return;

        var tenant = await _fixture.SeedOrderingTenantAsync(
            orderingFeatureEnabled: true,
            permissions: []); // No permissions

        var client = CatalogTestClient.For(_fixture, tenant);
        var response = await client.GetAsync("/api/tenant/orders");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task FeatureAndPermission_BothPresent_Returns200()
    {
        if (!_fixture.IsAvailable) return;

        var tenant = await _fixture.SeedOrderingTenantAsync(
            orderingFeatureEnabled: true,
            permissions: [OrderingPermissions.OrdersView.Key]);

        var client = CatalogTestClient.For(_fixture, tenant);
        var response = await client.GetAsync("/api/tenant/orders");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task CancelPermission_RequiredForCancellation()
    {
        if (!_fixture.IsAvailable) return;

        // User has OrdersView and OrdersStatusUpdate, but lacks OrdersCancel
        var tenant = await _fixture.SeedOrderingTenantAsync(
            orderingFeatureEnabled: true,
            permissions: [OrderingPermissions.OrdersView.Key, OrderingPermissions.OrdersStatusUpdate.Key]);

        var orderId = await SeedOrderAsync(tenant.TenantId);
        var client = CatalogTestClient.For(_fixture, tenant);

        var cancelResponse = await client.PostAsJsonAsync($"/api/tenant/orders/{orderId}/cancel", new CancelOrderRequest());
        Assert.Equal(HttpStatusCode.Forbidden, cancelResponse.StatusCode);
    }

    [Fact]
    public async Task CrossTenant_Read_Returns404()
    {
        if (!_fixture.IsAvailable) return;

        var tenantA = await _fixture.SeedOrderingTenantAsync(orderingFeatureEnabled: true, permissions: PostgresApiFixture.AllOrderingPermissions());
        var tenantB = await _fixture.SeedOrderingTenantAsync(orderingFeatureEnabled: true, permissions: PostgresApiFixture.AllOrderingPermissions());

        var orderBId = await SeedOrderAsync(tenantB.TenantId);

        var clientA = CatalogTestClient.For(_fixture, tenantA);
        var response = await clientA.GetAsync($"/api/tenant/orders/{orderBId}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task CrossTenant_StatusUpdate_Returns404()
    {
        if (!_fixture.IsAvailable) return;

        var tenantA = await _fixture.SeedOrderingTenantAsync(orderingFeatureEnabled: true, permissions: PostgresApiFixture.AllOrderingPermissions());
        var tenantB = await _fixture.SeedOrderingTenantAsync(orderingFeatureEnabled: true, permissions: PostgresApiFixture.AllOrderingPermissions());

        var orderBId = await SeedOrderAsync(tenantB.TenantId);

        var clientA = CatalogTestClient.For(_fixture, tenantA);
        var response = await clientA.PostAsJsonAsync($"/api/tenant/orders/{orderBId}/status", new TransitionOrderStatusRequest
        {
            TargetStatus = "Confirmed"
        });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task CrossTenant_Cancel_Returns404()
    {
        if (!_fixture.IsAvailable) return;

        var tenantA = await _fixture.SeedOrderingTenantAsync(orderingFeatureEnabled: true, permissions: PostgresApiFixture.AllOrderingPermissions());
        var tenantB = await _fixture.SeedOrderingTenantAsync(orderingFeatureEnabled: true, permissions: PostgresApiFixture.AllOrderingPermissions());

        var orderBId = await SeedOrderAsync(tenantB.TenantId);

        var clientA = CatalogTestClient.For(_fixture, tenantA);
        var response = await clientA.PostAsJsonAsync($"/api/tenant/orders/{orderBId}/cancel", new CancelOrderRequest());

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task PlatformAdmin_CanInspectTargetTenantOrders()
    {
        if (!_fixture.IsAvailable) return;

        var tenant = await _fixture.SeedOrderingTenantAsync(orderingFeatureEnabled: true);
        var orderId = await SeedOrderAsync(tenant.TenantId);

        var platformToken = _fixture.CreatePlatformAdminToken();
        var client = _fixture.Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", platformToken);

        var response = await client.GetAsync($"/api/platform/tenants/{tenant.TenantId}/orders/{orderId}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var detail = await response.Content.ReadFromJsonAsync<OrderDetailResponse>();
        Assert.NotNull(detail);
        Assert.Equal(orderId, detail.Id);
    }

    [Fact]
    public async Task PlatformAdmin_CanInspectOrdersEvenWhenFeatureIsDisabled()
    {
        if (!_fixture.IsAvailable) return;

        var tenant = await _fixture.SeedOrderingTenantAsync(orderingFeatureEnabled: true);
        var orderId = await SeedOrderAsync(tenant.TenantId);

        // Disable feature for tenant
        await _fixture.SetOrderingFeatureAsync(tenant.TenantId, isEnabled: false);

        var platformToken = _fixture.CreatePlatformAdminToken();
        var client = _fixture.Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", platformToken);

        // Platform endpoint succeeds (not blocked by feature gate)
        var response = await client.GetAsync($"/api/platform/tenants/{tenant.TenantId}/orders/{orderId}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var detail = await response.Content.ReadFromJsonAsync<OrderDetailResponse>();
        Assert.NotNull(detail);
        Assert.Equal(orderId, detail.Id);

        // Tenant endpoint is blocked
        var tenantClient = CatalogTestClient.For(_fixture, tenant.Host, _fixture.CreateTenantUserToken(tenant.TenantId, permissions: PostgresApiFixture.AllOrderingPermissions()));
        var tenantResponse = await tenantClient.GetAsync($"/api/tenant/orders/{orderId}");
        Assert.Equal(HttpStatusCode.Forbidden, tenantResponse.StatusCode);
    }

    [Fact]
    public async Task TenantUser_CannotCallPlatformOrderEndpoints_Returns403()
    {
        if (!_fixture.IsAvailable) return;

        var tenant = await _fixture.SeedOrderingTenantAsync(orderingFeatureEnabled: true, permissions: PostgresApiFixture.AllOrderingPermissions());
        var orderId = await SeedOrderAsync(tenant.TenantId);

        var client = CatalogTestClient.For(_fixture, tenant);
        var response = await client.GetAsync($"/api/platform/tenants/{tenant.TenantId}/orders/{orderId}");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }
}
