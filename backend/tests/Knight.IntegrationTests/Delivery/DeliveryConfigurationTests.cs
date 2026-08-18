using System.Net;
using System.Net.Http.Json;
using Delivery.Domain;
using Knight.Contracts.Common;
using Knight.Contracts.Delivery;
using Knight.IntegrationTests.Infrastructure;
using Xunit;

namespace Knight.IntegrationTests.Delivery;

[Collection(PostgresCollection.Name)]
public sealed class DeliveryConfigurationTests
{
    private readonly PostgresApiFixture _fixture;

    public DeliveryConfigurationTests(PostgresApiFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task DeliverySettings_CanBeReadAndUpdated()
    {
        if (!_fixture.IsAvailable) return;

        var context = await _fixture.SeedDeliveryTenantAsync(
            deliveryFeatureEnabled: true,
            permissions: PostgresApiFixture.AllDeliveryPermissions());

        var client = _fixture.Factory.CreateClient();
        client.DefaultRequestHeaders.Host = context.Host;
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", context.Token);

        // Get default settings
        var getRes = await client.GetAsync("/api/tenant/delivery/settings");
        Assert.Equal(HttpStatusCode.OK, getRes.StatusCode);
        var settings = await getRes.Content.ReadFromJsonAsync<TenantDeliverySettingsResponse>();
        Assert.NotNull(settings);
        Assert.True(settings.IsAcceptingDeliveryOrders);
        Assert.Null(settings.DefaultMinimumOrderSubtotal);

        // Update settings
        var updateReq = new UpdateTenantDeliverySettingsRequest
        {
            IsAcceptingDeliveryOrders = false,
            DefaultMinimumOrderSubtotal = 15.00m
        };
        var updateRes = await client.PutAsJsonAsync("/api/tenant/delivery/settings", updateReq);
        Assert.Equal(HttpStatusCode.OK, updateRes.StatusCode);
        var updated = await updateRes.Content.ReadFromJsonAsync<TenantDeliverySettingsResponse>();
        Assert.NotNull(updated);
        Assert.False(updated.IsAcceptingDeliveryOrders);
        Assert.Equal(15.00m, updated.DefaultMinimumOrderSubtotal);

        // Verify persistence
        var getRes2 = await client.GetAsync("/api/tenant/delivery/settings");
        Assert.Equal(HttpStatusCode.OK, getRes2.StatusCode);
        var settings2 = await getRes2.Content.ReadFromJsonAsync<TenantDeliverySettingsResponse>();
        Assert.NotNull(settings2);
        Assert.False(settings2.IsAcceptingDeliveryOrders);
        Assert.Equal(15.00m, settings2.DefaultMinimumOrderSubtotal);
    }

    [Fact]
    public async Task DeliveryZones_FullCrudAndLifecycle_WorksCorrectly()
    {
        if (!_fixture.IsAvailable) return;

        var context = await _fixture.SeedDeliveryTenantAsync(
            deliveryFeatureEnabled: true,
            permissions: PostgresApiFixture.AllDeliveryPermissions());

        var client = _fixture.Factory.CreateClient();
        client.DefaultRequestHeaders.Host = context.Host;
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", context.Token);

        // Create Zone
        var createReq = new CreateDeliveryZoneRequest
        {
            Name = "Downtown Core",
            Fee = 5.00m,
            MinimumOrderSubtotal = 20.00m,
            DisplayOrder = 1
        };
        var createRes = await client.PostAsJsonAsync("/api/tenant/delivery/zones", createReq);
        Assert.Equal(HttpStatusCode.Created, createRes.StatusCode);
        var created = await createRes.Content.ReadFromJsonAsync<DeliveryZoneResponse>();
        Assert.NotNull(created);
        Assert.Equal("Downtown Core", created.Name);
        Assert.Equal(5.00m, created.Fee);
        Assert.Equal(20.00m, created.MinimumOrderSubtotal);
        Assert.Equal("Active", created.Status);

        // Get by ID
        var getRes = await client.GetAsync($"/api/tenant/delivery/zones/{created.Id}");
        Assert.Equal(HttpStatusCode.OK, getRes.StatusCode);
        var fetched = await getRes.Content.ReadFromJsonAsync<DeliveryZoneResponse>();
        Assert.NotNull(fetched);
        Assert.Equal(created.Id, fetched.Id);

        // Update Zone
        var updateReq = new UpdateDeliveryZoneRequest
        {
            Name = "Downtown Core & Waterfront",
            Fee = 6.50m,
            MinimumOrderSubtotal = 25.00m,
            DisplayOrder = 2
        };
        var updateRes = await client.PutAsJsonAsync($"/api/tenant/delivery/zones/{created.Id}", updateReq);
        Assert.Equal(HttpStatusCode.OK, updateRes.StatusCode);
        var updated = await updateRes.Content.ReadFromJsonAsync<DeliveryZoneResponse>();
        Assert.NotNull(updated);
        Assert.Equal("Downtown Core & Waterfront", updated.Name);
        Assert.Equal(6.50m, updated.Fee);
        Assert.Equal(25.00m, updated.MinimumOrderSubtotal);

        // Archive Zone
        var archiveRes = await client.PostAsync($"/api/tenant/delivery/zones/{created.Id}/archive", null);
        Assert.Equal(HttpStatusCode.OK, archiveRes.StatusCode);
        var archived = await archiveRes.Content.ReadFromJsonAsync<DeliveryZoneResponse>();
        Assert.NotNull(archived);
        Assert.Equal("Archived", archived.Status);
        Assert.NotNull(archived.ArchivedAt);

        // Restore Zone
        var restoreRes = await client.PostAsync($"/api/tenant/delivery/zones/{created.Id}/restore", null);
        Assert.Equal(HttpStatusCode.OK, restoreRes.StatusCode);
        var restored = await restoreRes.Content.ReadFromJsonAsync<DeliveryZoneResponse>();
        Assert.NotNull(restored);
        Assert.Equal("Active", restored.Status);
        Assert.Null(restored.ArchivedAt);
    }

    [Fact]
    public async Task DeliveryZones_ListAndFiltering_EnforcesPaginationAndStatus()
    {
        if (!_fixture.IsAvailable) return;

        var context = await _fixture.SeedDeliveryTenantAsync(
            deliveryFeatureEnabled: true,
            permissions: PostgresApiFixture.AllDeliveryPermissions());

        var client = _fixture.Factory.CreateClient();
        client.DefaultRequestHeaders.Host = context.Host;
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", context.Token);

        // Seed 3 zones
        var zone1 = await _fixture.SeedDeliveryZoneAsync(context.TenantId, "Zone Alpha", 3.00m, 10.00m, 1);
        var zone2 = await _fixture.SeedDeliveryZoneAsync(context.TenantId, "Zone Beta", 5.00m, 15.00m, 2);
        var zone3 = await _fixture.SeedDeliveryZoneAsync(context.TenantId, "Zone Gamma", 8.00m, 20.00m, 3);

        // Archive Zone Beta
        await client.PostAsync($"/api/tenant/delivery/zones/{zone2.Id}/archive", null);

        // List all
        var listAll = await client.GetFromJsonAsync<PagedResponse<DeliveryZoneResponse>>("/api/tenant/delivery/zones?page=1&pageSize=10");
        Assert.NotNull(listAll);
        Assert.Equal(3, listAll.TotalCount);

        // List active only
        var listActive = await client.GetFromJsonAsync<PagedResponse<DeliveryZoneResponse>>("/api/tenant/delivery/zones?page=1&pageSize=10&status=Active");
        Assert.NotNull(listActive);
        Assert.Equal(2, listActive.TotalCount);
        Assert.DoesNotContain(listActive.Items, z => z.Id == zone2.Id);

        // List archived only
        var listArchived = await client.GetFromJsonAsync<PagedResponse<DeliveryZoneResponse>>("/api/tenant/delivery/zones?page=1&pageSize=10&status=Archived");
        Assert.NotNull(listArchived);
        Assert.Equal(1, listArchived.TotalCount);
        Assert.Equal(zone2.Id, listArchived.Items.First().Id);
    }

    [Fact]
    public async Task DeliveryZones_StrictTenantIsolation_PreventsCrossTenantAccess()
    {
        if (!_fixture.IsAvailable) return;

        var tenantA = await _fixture.SeedDeliveryTenantAsync(deliveryFeatureEnabled: true, permissions: PostgresApiFixture.AllDeliveryPermissions());
        var tenantB = await _fixture.SeedDeliveryTenantAsync(deliveryFeatureEnabled: true, permissions: PostgresApiFixture.AllDeliveryPermissions());

        var zoneA = await _fixture.SeedDeliveryZoneAsync(tenantA.TenantId, "Zone in Tenant A", 4.00m);

        var clientB = _fixture.Factory.CreateClient();
        clientB.DefaultRequestHeaders.Host = tenantB.Host;
        clientB.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", tenantB.Token);

        // Tenant B cannot get Tenant A's zone
        var getRes = await clientB.GetAsync($"/api/tenant/delivery/zones/{zoneA.Id}");
        Assert.Equal(HttpStatusCode.NotFound, getRes.StatusCode);

        // Tenant B cannot update Tenant A's zone
        var updateRes = await clientB.PutAsJsonAsync($"/api/tenant/delivery/zones/{zoneA.Id}", new UpdateDeliveryZoneRequest
        {
            Name = "Hacked Zone",
            Fee = 0m,
            DisplayOrder = 1
        });
        Assert.Equal(HttpStatusCode.NotFound, updateRes.StatusCode);
    }
}
