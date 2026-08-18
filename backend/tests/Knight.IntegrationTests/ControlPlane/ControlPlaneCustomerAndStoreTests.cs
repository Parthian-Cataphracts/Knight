using System.Net;
using System.Net.Http.Json;
using AccessControl.Domain;
using Knight.IntegrationTests.Infrastructure;

namespace Knight.IntegrationTests.ControlPlane;

[Collection(PostgresCollection.Name)]
public sealed class ControlPlaneCustomerAndStoreTests
{
    private const string Password = "correct horse battery staple";

    private readonly PostgresApiFixture _fixture;

    public ControlPlaneCustomerAndStoreTests(PostgresApiFixture fixture)
    {
        _fixture = fixture;
    }

    private static string Email() => $"user-{Guid.NewGuid():n}@knight.test";

    private async Task<HttpClient> PlatformClientAsync(string role = SystemRoles.Admin)
    {
        var email = Email();
        await _fixture.SeedUserAsync(email, Password, role);
        return _fixture.CreateClient(await _fixture.SignInAsync(email, Password));
    }

    [Fact]
    public async Task AnAdminCanRunTheWholePhaseOneFlow()
    {
        if (!_fixture.IsAvailable) return;

        var client = await PlatformClientAsync();
        var suffix = Guid.NewGuid().ToString("n")[..8];

        // Create a customer.
        var created = await client.PostAsJsonAsync("/api/v1/customers", new
        {
            name = $"Cafe {suffix}",
            contactEmail = $"owner-{suffix}@example.test",
        });

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var customer = await created.Content.ReadFromJsonAsync<CustomerBody>();
        Assert.Equal("Prospect", customer!.Status);

        // Activate it.
        var activated = await client.PostAsync($"/api/v1/customers/{customer.Id}/activate", null);
        Assert.Equal("Active", (await activated.Content.ReadFromJsonAsync<CustomerBody>())!.Status);

        // Register a store for it.
        var storeResponse = await client.PostAsJsonAsync("/api/v1/stores", new
        {
            customerId = customer.Id,
            name = $"Store {suffix}",
            slug = $"store-{suffix}",
            primaryDomain = $"{suffix}.example.test",
            environment = "Production",
            hostingModel = "SharedManaged",
        });

        Assert.Equal(HttpStatusCode.Created, storeResponse.StatusCode);
        var store = await storeResponse.Content.ReadFromJsonAsync<StoreBody>();
        Assert.Equal("Provisioning", store!.Status);
        Assert.Equal("NotRegistered", store.IntegrationStatus);

        // Issue credentials.
        var credentialResponse = await client.PostAsync($"/api/v1/stores/{store.Id}/credentials", null);
        Assert.Equal(HttpStatusCode.Created, credentialResponse.StatusCode);

        var credential = await credentialResponse.Content.ReadFromJsonAsync<IssuedCredentialBody>();
        Assert.False(string.IsNullOrWhiteSpace(credential!.ClientSecret));

        // The secret is never readable again: the store representation lists the
        // credential by state only.
        var reread = await (await client.GetAsync($"/api/v1/stores/{store.Id}")).Content.ReadFromJsonAsync<StoreBody>();
        Assert.Single(reread!.Credentials);
        Assert.Equal("Active", reread.Credentials.First().State);
        Assert.DoesNotContain(credential.ClientSecret, await (await client.GetAsync($"/api/v1/stores/{store.Id}")).Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task DuplicateContactEmail_IsAConflict()
    {
        if (!_fixture.IsAvailable) return;

        var client = await PlatformClientAsync();
        var email = $"dup-{Guid.NewGuid():n}@example.test";

        await client.PostAsJsonAsync("/api/v1/customers", new { name = "First", contactEmail = email });
        var second = await client.PostAsJsonAsync("/api/v1/customers", new { name = "Second", contactEmail = email });

        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
    }

    [Fact]
    public async Task AnInvalidContactEmail_IsAValidationFailure()
    {
        if (!_fixture.IsAvailable) return;

        var client = await PlatformClientAsync();

        var response = await client.PostAsJsonAsync("/api/v1/customers", new { name = "Cafe", contactEmail = "not-an-email" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task AnUnrecognisedStatusFilter_IsRejectedRatherThanIgnored()
    {
        if (!_fixture.IsAvailable) return;

        var client = await PlatformClientAsync();

        var response = await client.GetAsync("/api/v1/customers?status=Whatever");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task ArchivingACustomerIsTerminal()
    {
        if (!_fixture.IsAvailable) return;

        var client = await PlatformClientAsync();
        var customerId = await _fixture.SeedCustomerAsync();

        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync($"/api/v1/customers/{customerId}/archive", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsync($"/api/v1/customers/{customerId}/activate", null)).StatusCode);
    }

    [Fact]
    public async Task ADuplicateStoreDomain_IsAConflict()
    {
        if (!_fixture.IsAvailable) return;

        var client = await PlatformClientAsync();
        var customerId = await _fixture.SeedCustomerAsync();
        var suffix = Guid.NewGuid().ToString("n")[..8];

        object Payload(string slug) => new
        {
            customerId,
            name = "Store",
            slug,
            primaryDomain = $"{suffix}.example.test",
            environment = "Production",
            hostingModel = "SharedManaged",
        };

        await client.PostAsJsonAsync("/api/v1/stores", Payload($"a-{suffix}"));
        var second = await client.PostAsJsonAsync("/api/v1/stores", Payload($"b-{suffix}"));

        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
    }

    [Fact]
    public async Task RotatingACredential_LeavesThePreviousOneUsableForItsGraceWindow()
    {
        if (!_fixture.IsAvailable) return;

        var client = await PlatformClientAsync();
        var customerId = await _fixture.SeedCustomerAsync();
        var storeId = await _fixture.SeedStoreAsync(customerId);

        var first = await (await client.PostAsync($"/api/v1/stores/{storeId}/credentials", null))
            .Content.ReadFromJsonAsync<IssuedCredentialBody>();

        var rotated = await client.PostAsync($"/api/v1/stores/{storeId}/credentials/{first!.Id}/rotate", null);
        Assert.Equal(HttpStatusCode.OK, rotated.StatusCode);

        var replacement = await rotated.Content.ReadFromJsonAsync<IssuedCredentialBody>();
        Assert.NotEqual(first.ClientSecret, replacement!.ClientSecret);

        var store = await (await client.GetAsync($"/api/v1/stores/{storeId}")).Content.ReadFromJsonAsync<StoreBody>();
        Assert.Equal("GracePeriod", store!.Credentials.Single(c => c.Id == first.Id).State);
        Assert.Equal("Active", store.Credentials.Single(c => c.Id == replacement.Id).State);
    }

    [Fact]
    public async Task RevokingACredential_MarksItRevoked()
    {
        if (!_fixture.IsAvailable) return;

        var client = await PlatformClientAsync();
        var customerId = await _fixture.SeedCustomerAsync();
        var storeId = await _fixture.SeedStoreAsync(customerId);

        var credential = await (await client.PostAsync($"/api/v1/stores/{storeId}/credentials", null))
            .Content.ReadFromJsonAsync<IssuedCredentialBody>();

        Assert.Equal(
            HttpStatusCode.NoContent,
            (await client.DeleteAsync($"/api/v1/stores/{storeId}/credentials/{credential!.Id}")).StatusCode);

        var store = await (await client.GetAsync($"/api/v1/stores/{storeId}")).Content.ReadFromJsonAsync<StoreBody>();
        Assert.Equal("Revoked", store!.Credentials.Single().State);
    }

    [Fact]
    public async Task ArchivingAStore_RevokesEveryCredential()
    {
        if (!_fixture.IsAvailable) return;

        var client = await PlatformClientAsync();
        var customerId = await _fixture.SeedCustomerAsync();
        var storeId = await _fixture.SeedStoreAsync(customerId);

        await client.PostAsync($"/api/v1/stores/{storeId}/credentials", null);
        await client.PostAsync($"/api/v1/stores/{storeId}/archive", null);

        var store = await (await client.GetAsync($"/api/v1/stores/{storeId}")).Content.ReadFromJsonAsync<StoreBody>();
        Assert.All(store!.Credentials, credential => Assert.Equal("Revoked", credential.State));
    }

    [Fact]
    public async Task EveryMutationIsAudited()
    {
        if (!_fixture.IsAvailable) return;

        var client = await PlatformClientAsync();
        var suffix = Guid.NewGuid().ToString("n")[..8];

        var customer = await (await client.PostAsJsonAsync("/api/v1/customers", new
        {
            name = $"Cafe {suffix}",
            contactEmail = $"owner-{suffix}@example.test",
        })).Content.ReadFromJsonAsync<CustomerBody>();

        var audit = await client.GetAsync($"/api/v1/audit-logs?targetType=Customer&pageSize=100");
        Assert.Equal(HttpStatusCode.OK, audit.StatusCode);

        var body = await audit.Content.ReadAsStringAsync();
        Assert.Contains("customer.created", body);
        Assert.Contains(customer!.Id.ToString(), body);
    }

    [Fact]
    public async Task ACredentialAuditEntryNeverCarriesTheSecret()
    {
        if (!_fixture.IsAvailable) return;

        var client = await PlatformClientAsync();
        var customerId = await _fixture.SeedCustomerAsync();
        var storeId = await _fixture.SeedStoreAsync(customerId);

        var credential = await (await client.PostAsync($"/api/v1/stores/{storeId}/credentials", null))
            .Content.ReadFromJsonAsync<IssuedCredentialBody>();

        var audit = await (await client.GetAsync("/api/v1/audit-logs?targetType=StoreCredential&pageSize=100")).Content.ReadAsStringAsync();

        Assert.Contains("store.credential.issued", audit);
        Assert.DoesNotContain(credential!.ClientSecret, audit);
    }

    [Fact]
    public async Task ARoleWithoutThePermission_IsForbidden()
    {
        if (!_fixture.IsAvailable) return;

        // Support is read-mostly across customers: it may look at customers but
        // not create them (docs/authorization.md section 1).
        var client = await PlatformClientAsync(SystemRoles.Support);

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/customers")).StatusCode);

        var created = await client.PostAsJsonAsync("/api/v1/customers", new
        {
            name = "Cafe",
            contactEmail = $"owner-{Guid.NewGuid():n}@example.test",
        });

        Assert.Equal(HttpStatusCode.Forbidden, created.StatusCode);
    }

    [Fact]
    public async Task ACustomerCannotIssueCredentialsWithoutThatPermission()
    {
        if (!_fixture.IsAvailable) return;

        var customerId = await _fixture.SeedCustomerAsync();
        var storeId = await _fixture.SeedStoreAsync(customerId);

        var email = Email();
        await _fixture.SeedUserAsync(email, Password, SystemRoles.CustomerStaff, customerId);
        var client = _fixture.CreateClient(await _fixture.SignInAsync(email, Password));

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/api/v1/stores/{storeId}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsync($"/api/v1/stores/{storeId}/credentials", null)).StatusCode);
    }

    private sealed record CustomerBody(Guid Id, string Name, string ContactEmail, string Status);

    private sealed record StoreBody(
        Guid Id,
        Guid CustomerId,
        string Status,
        string IntegrationStatus,
        IReadOnlyCollection<CredentialBody> Credentials);

    private sealed record CredentialBody(Guid Id, string ClientId, string State);

    private sealed record IssuedCredentialBody(Guid Id, string ClientId, string ClientSecret);
}
