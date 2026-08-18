using System.Net;
using System.Net.Http.Json;
using Knight.Contracts.Fulfillment;
using Knight.IntegrationTests.Infrastructure;
using Xunit;

namespace Knight.IntegrationTests.Fulfillment;

[Collection(PostgresCollection.Name)]
public sealed class FulfillmentSettingsTests
{
    private readonly PostgresApiFixture _fixture;

    public FulfillmentSettingsTests(PostgresApiFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task FulfillmentSettings_CanBeReadAndUpdated()
    {
        if (!_fixture.IsAvailable) return;

        var context = await _fixture.SeedDeliveryTenantAsync(
            deliveryFeatureEnabled: false, // Delivery disabled - fulfillment operates independently
            permissions: PostgresApiFixture.AllFulfillmentPermissions());

        var client = _fixture.Factory.CreateClient();
        client.DefaultRequestHeaders.Host = context.Host;
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", context.Token);

        // Get default settings
        var getRes = await client.GetAsync("/api/tenant/fulfillment/settings");
        Assert.Equal(HttpStatusCode.OK, getRes.StatusCode);
        var settings = await getRes.Content.ReadFromJsonAsync<TenantFulfillmentSettingsResponse>();
        Assert.NotNull(settings);
        Assert.True(settings.PickupEnabled);

        // Update settings
        var updateRes = await client.PutAsJsonAsync("/api/tenant/fulfillment/settings", new UpdateTenantFulfillmentSettingsRequest
        {
            PickupEnabled = false
        });
        Assert.Equal(HttpStatusCode.OK, updateRes.StatusCode);
        var updated = await updateRes.Content.ReadFromJsonAsync<TenantFulfillmentSettingsResponse>();
        Assert.NotNull(updated);
        Assert.False(updated.PickupEnabled);

        // Verify persistence
        var getRes2 = await client.GetAsync("/api/tenant/fulfillment/settings");
        Assert.Equal(HttpStatusCode.OK, getRes2.StatusCode);
        var settings2 = await getRes2.Content.ReadFromJsonAsync<TenantFulfillmentSettingsResponse>();
        Assert.NotNull(settings2);
        Assert.False(settings2.PickupEnabled);
    }

    [Fact]
    public async Task FulfillmentSettings_WhenMissingPermission_Returns403()
    {
        if (!_fixture.IsAvailable) return;

        var context = await _fixture.SeedDeliveryTenantAsync(
            deliveryFeatureEnabled: true,
            permissions: []);

        var client = _fixture.Factory.CreateClient();
        client.DefaultRequestHeaders.Host = context.Host;
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", context.Token);

        var getRes = await client.GetAsync("/api/tenant/fulfillment/settings");
        Assert.Equal(HttpStatusCode.Forbidden, getRes.StatusCode);
    }

    [Fact]
    public async Task PlatformAdminEndpoints_CanManageFulfillmentSettings()
    {
        if (!_fixture.IsAvailable) return;

        var context = await _fixture.SeedDeliveryTenantAsync(
            deliveryFeatureEnabled: false,
            permissions: []);

        var platformToken = _fixture.CreatePlatformAdminToken();
        var client = _fixture.Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", platformToken);

        var getRes = await client.GetAsync($"/api/platform/tenants/{context.TenantId}/fulfillment/settings");
        Assert.Equal(HttpStatusCode.OK, getRes.StatusCode);

        var updateRes = await client.PutAsJsonAsync($"/api/platform/tenants/{context.TenantId}/fulfillment/settings", new UpdateTenantFulfillmentSettingsRequest
        {
            PickupEnabled = false
        });
        Assert.Equal(HttpStatusCode.OK, updateRes.StatusCode);
    }
}
