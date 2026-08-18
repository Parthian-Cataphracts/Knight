using System.Net;
using System.Net.Http.Json;
using Delivery;
using Knight.Contracts.Delivery;
using Knight.IntegrationTests.Infrastructure;
using Xunit;

namespace Knight.IntegrationTests.Delivery;

[Collection(PostgresCollection.Name)]
public sealed class DeliverySecurityTests
{
    private readonly PostgresApiFixture _fixture;

    public DeliverySecurityTests(PostgresApiFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task TenantDeliveryEndpoints_WhenDeliveryFeatureDisabled_Return403()
    {
        if (!_fixture.IsAvailable) return;

        var context = await _fixture.SeedDeliveryTenantAsync(
            deliveryFeatureEnabled: false,
            permissions: PostgresApiFixture.AllDeliveryPermissions());

        var client = _fixture.Factory.CreateClient();
        client.DefaultRequestHeaders.Host = context.Host;
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", context.Token);

        var settingsRes = await client.GetAsync("/api/tenant/delivery/settings");
        Assert.Equal(HttpStatusCode.Forbidden, settingsRes.StatusCode);

        var zonesRes = await client.GetAsync("/api/tenant/delivery/zones");
        Assert.Equal(HttpStatusCode.Forbidden, zonesRes.StatusCode);
    }

    [Fact]
    public async Task TenantDeliveryEndpoints_WhenMissingPermission_Return403()
    {
        if (!_fixture.IsAvailable) return;

        // Tenant user with NO delivery permissions
        var context = await _fixture.SeedDeliveryTenantAsync(
            deliveryFeatureEnabled: true,
            permissions: []);

        var client = _fixture.Factory.CreateClient();
        client.DefaultRequestHeaders.Host = context.Host;
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", context.Token);

        var settingsRes = await client.GetAsync("/api/tenant/delivery/settings");
        Assert.Equal(HttpStatusCode.Forbidden, settingsRes.StatusCode);

        var zonesRes = await client.GetAsync("/api/tenant/delivery/zones");
        Assert.Equal(HttpStatusCode.Forbidden, zonesRes.StatusCode);
    }

    [Fact]
    public async Task PlatformAdminEndpoints_CanInspectDelivery_EvenWhenTenantFeatureDisabled()
    {
        if (!_fixture.IsAvailable) return;

        var context = await _fixture.SeedDeliveryTenantAsync(
            deliveryFeatureEnabled: false,
            permissions: PostgresApiFixture.AllDeliveryPermissions());

        var platformToken = _fixture.CreatePlatformAdminToken();
        var client = _fixture.Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", platformToken);

        // Platform admin can read settings
        var settingsRes = await client.GetAsync($"/api/platform/tenants/{context.TenantId}/delivery/settings");
        Assert.Equal(HttpStatusCode.OK, settingsRes.StatusCode);

        // Platform admin can create a zone
        var createRes = await client.PostAsJsonAsync($"/api/platform/tenants/{context.TenantId}/delivery/zones", new CreateDeliveryZoneRequest
        {
            Name = "Platform Seeded Zone",
            Fee = 4.00m,
            DisplayOrder = 1
        });
        Assert.Equal(HttpStatusCode.Created, createRes.StatusCode);
    }
}
