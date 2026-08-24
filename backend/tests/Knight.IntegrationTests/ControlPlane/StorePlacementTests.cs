using System.Net;
using System.Net.Http.Json;
using AccessControl.Domain;
using Knight.IntegrationTests.Infrastructure;

namespace Knight.IntegrationTests.ControlPlane;

/// <summary>
/// Which machine a customer's store runs on.
///
/// The rule that matters commercially is the third test here: a dedicated
/// machine hosts one customer's stores and nobody else's. That is what a
/// customer is paying for when they buy dedicated infrastructure, and it cannot
/// be left to whoever is filling in the form to remember.
///
/// The first two exist because placement used to be a field on the profile
/// update. Any caller that edited a store's name without sending the server id
/// back - which is what the dashboard did - silently took the store off its
/// machine. It is its own operation now, and these fail if it ever goes back.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class StorePlacementTests
{
    private const string Password = "correct horse battery staple";

    private readonly PostgresApiFixture _fixture;

    public StorePlacementTests(PostgresApiFixture fixture)
    {
        _fixture = fixture;
    }

    private static string Suffix() => Guid.NewGuid().ToString("n")[..8];

    private async Task<HttpClient> AdminAsync()
    {
        var email = $"user-{Guid.NewGuid():n}@knight.test";
        await _fixture.SeedUserAsync(email, Password, SystemRoles.Admin);
        return _fixture.CreateClient(await _fixture.SignInAsync(email, Password));
    }

    private static async Task<Guid> RegisterServerAsync(
        HttpClient client,
        string environment = "Production",
        string hostingModel = "DedicatedManaged")
    {
        var response = await client.PostAsJsonAsync("/api/v1/servers", new
        {
            name = $"host-{Suffix()}",
            hostingModel,
            environment,
            provider = "hetzner",
            region = "fsn1",
            ipAddress = "203.0.113.10",
        });

        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<ServerBody>())!.Id;
    }

    private static async Task<Guid> RegisterStoreAsync(
        HttpClient client,
        Guid customerId,
        string environment = "Production",
        Guid? serverId = null)
    {
        var suffix = Suffix();
        var response = await client.PostAsJsonAsync("/api/v1/stores", new
        {
            customerId,
            name = $"Store {suffix}",
            slug = $"store-{suffix}",
            primaryDomain = $"{suffix}.example.test",
            environment,
            hostingModel = "DedicatedManaged",
            serverId,
        });

        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<StoreBody>())!.Id;
    }

    [Fact]
    public async Task AStoreCanBePlacedOnAServerWhenItIsRegistered()
    {
        if (!_fixture.IsAvailable) return;

        var client = await AdminAsync();
        var customerId = await _fixture.SeedCustomerAsync();
        var serverId = await RegisterServerAsync(client);

        var storeId = await RegisterStoreAsync(client, customerId, serverId: serverId);

        var store = await client.GetFromJsonAsync<StoreBody>($"/api/v1/stores/{storeId}");
        Assert.Equal(serverId, store!.ServerId);
    }

    [Fact]
    public async Task EditingTheNameLeavesTheStoreWhereItIs()
    {
        if (!_fixture.IsAvailable) return;

        var client = await AdminAsync();
        var customerId = await _fixture.SeedCustomerAsync();
        var serverId = await RegisterServerAsync(client);
        var storeId = await RegisterStoreAsync(client, customerId, serverId: serverId);

        var before = await client.GetFromJsonAsync<StoreBody>($"/api/v1/stores/{storeId}");

        var renamed = await client.PatchAsJsonAsync($"/api/v1/stores/{storeId}", new
        {
            name = "A different name",
            primaryDomain = before!.PrimaryDomain,
        });

        Assert.Equal(HttpStatusCode.OK, renamed.StatusCode);

        var after = await renamed.Content.ReadFromJsonAsync<StoreBody>();
        Assert.Equal("A different name", after!.Name);
        Assert.Equal(serverId, after.ServerId);
    }

    [Fact]
    public async Task AServerDedicatedToAnotherCustomerIsRefused()
    {
        if (!_fixture.IsAvailable) return;

        var client = await AdminAsync();
        var theirs = await _fixture.SeedCustomerAsync();
        var ours = await _fixture.SeedCustomerAsync();

        var serverId = await RegisterServerAsync(client);
        var dedicated = await client.PutAsJsonAsync($"/api/v1/servers/{serverId}/dedication", new { customerId = theirs });
        Assert.Equal(HttpStatusCode.OK, dedicated.StatusCode);

        // At registration.
        var suffix = Suffix();
        var registered = await client.PostAsJsonAsync("/api/v1/stores", new
        {
            customerId = ours,
            name = $"Store {suffix}",
            slug = $"store-{suffix}",
            primaryDomain = $"{suffix}.example.test",
            environment = "Production",
            hostingModel = "DedicatedManaged",
            serverId,
        });

        Assert.Equal(HttpStatusCode.Conflict, registered.StatusCode);

        // And on a later move, so the rule cannot be walked around in two steps.
        var storeId = await RegisterStoreAsync(client, ours);
        var moved = await client.PutAsJsonAsync($"/api/v1/stores/{storeId}/server", new { serverId });

        Assert.Equal(HttpStatusCode.Conflict, moved.StatusCode);
    }

    [Fact]
    public async Task AServerInAnotherEnvironmentIsRefused()
    {
        if (!_fixture.IsAvailable) return;

        var client = await AdminAsync();
        var customerId = await _fixture.SeedCustomerAsync();
        var stagingServer = await RegisterServerAsync(client, environment: "Staging");
        var storeId = await RegisterStoreAsync(client, customerId, environment: "Production");

        var moved = await client.PutAsJsonAsync($"/api/v1/stores/{storeId}/server", new { serverId = stagingServer });

        Assert.Equal(HttpStatusCode.Conflict, moved.StatusCode);
    }

    [Fact]
    public async Task AStoreCanBeTakenOffItsServer()
    {
        if (!_fixture.IsAvailable) return;

        var client = await AdminAsync();
        var customerId = await _fixture.SeedCustomerAsync();
        var serverId = await RegisterServerAsync(client);
        var storeId = await RegisterStoreAsync(client, customerId, serverId: serverId);

        var cleared = await client.PutAsJsonAsync($"/api/v1/stores/{storeId}/server", new { serverId = (Guid?)null });

        Assert.Equal(HttpStatusCode.OK, cleared.StatusCode);
        Assert.Null((await cleared.Content.ReadFromJsonAsync<StoreBody>())!.ServerId);
    }

    private sealed record ServerBody(Guid Id, string Name, Guid? DedicatedCustomerId);

    private sealed record StoreBody(Guid Id, string Name, string PrimaryDomain, Guid? ServerId);
}
