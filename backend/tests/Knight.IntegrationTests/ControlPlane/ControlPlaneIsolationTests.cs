using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using AccessControl.Domain;
using Knight.IntegrationTests.Infrastructure;
using Microsoft.IdentityModel.Tokens;

namespace Knight.IntegrationTests.ControlPlane;

/// <summary>
/// The release-blocking isolation suite from docs/authorization.md section 6.
/// Customer A must not be able to read or mutate anything belonging to Customer
/// B, and must learn nothing about B's existence from the attempt: a resource
/// hidden by the filter reads as 404, never 403.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class ControlPlaneIsolationTests
{
    private const string Password = "correct horse battery staple";

    private readonly PostgresApiFixture _fixture;

    public ControlPlaneIsolationTests(PostgresApiFixture fixture)
    {
        _fixture = fixture;
    }

    private static string Email() => $"user-{Guid.NewGuid():n}@knight.test";

    private async Task<(Guid CustomerId, HttpClient Client)> OwnerAsync()
    {
        var customerId = await _fixture.SeedCustomerAsync();
        var email = Email();
        await _fixture.SeedUserAsync(email, Password, SystemRoles.CustomerOwner, customerId);

        return (customerId, _fixture.CreateClient(await _fixture.SignInAsync(email, Password)));
    }

    [Fact]
    public async Task ACustomerListsOnlyItself()
    {
        if (!_fixture.IsAvailable) return;

        var (customerId, client) = await OwnerAsync();
        await _fixture.SeedCustomerAsync();

        var body = await (await client.GetAsync("/api/v1/customers")).Content.ReadFromJsonAsync<PagedBody<CustomerBody>>();

        Assert.Equal(1, body!.TotalCount);
        Assert.Equal(customerId, body.Items.Single().Id);
    }

    [Fact]
    public async Task AnotherCustomerReadsAsNotFound_NotForbidden()
    {
        if (!_fixture.IsAvailable) return;

        var (_, client) = await OwnerAsync();
        var otherCustomerId = await _fixture.SeedCustomerAsync();

        var response = await client.GetAsync($"/api/v1/customers/{otherCustomerId}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task AnotherCustomerCannotBeMutated()
    {
        if (!_fixture.IsAvailable) return;

        var (_, client) = await OwnerAsync();
        var otherCustomerId = await _fixture.SeedCustomerAsync();

        var updated = await client.PatchAsJsonAsync($"/api/v1/customers/{otherCustomerId}", new
        {
            name = "Taken over",
            contactEmail = $"new-{Guid.NewGuid():n}@example.test",
        });

        Assert.Equal(HttpStatusCode.NotFound, updated.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsync($"/api/v1/customers/{otherCustomerId}/suspend", null)).StatusCode);
    }

    [Fact]
    public async Task StoresOfAnotherCustomerAreInvisible()
    {
        if (!_fixture.IsAvailable) return;

        var (customerId, client) = await OwnerAsync();
        await _fixture.SeedStoreAsync(customerId);

        var otherCustomerId = await _fixture.SeedCustomerAsync();
        var otherStoreId = await _fixture.SeedStoreAsync(otherCustomerId);

        var list = await (await client.GetAsync("/api/v1/stores")).Content.ReadFromJsonAsync<PagedBody<StoreBody>>();

        Assert.Equal(1, list!.TotalCount);
        Assert.DoesNotContain(list.Items, store => store.Id == otherStoreId);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/v1/stores/{otherStoreId}")).StatusCode);
    }

    [Fact]
    public async Task FilteringByAnotherCustomerIdReturnsNothing()
    {
        if (!_fixture.IsAvailable) return;

        var (_, client) = await OwnerAsync();
        var otherCustomerId = await _fixture.SeedCustomerAsync();
        await _fixture.SeedStoreAsync(otherCustomerId);

        // Asking directly for another customer's stores must not widen the scope.
        var list = await (await client.GetAsync($"/api/v1/stores?customerId={otherCustomerId}"))
            .Content.ReadFromJsonAsync<PagedBody<StoreBody>>();

        Assert.Equal(0, list!.TotalCount);
    }

    [Fact]
    public async Task CredentialsOfAnotherCustomersStoreCannotBeIssuedOrRevoked()
    {
        if (!_fixture.IsAvailable) return;

        var (_, client) = await OwnerAsync();
        var otherCustomerId = await _fixture.SeedCustomerAsync();
        var otherStoreId = await _fixture.SeedStoreAsync(otherCustomerId);

        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsync($"/api/v1/stores/{otherStoreId}/credentials", null)).StatusCode);
        Assert.Equal(
            HttpStatusCode.NotFound,
            (await client.DeleteAsync($"/api/v1/stores/{otherStoreId}/credentials/{Guid.NewGuid()}")).StatusCode);
    }

    [Fact]
    public async Task AuditEntriesOfAnotherCustomerAreInvisible()
    {
        if (!_fixture.IsAvailable) return;

        var platformEmail = Email();
        await _fixture.SeedUserAsync(platformEmail, Password, SystemRoles.Admin);
        var platform = _fixture.CreateClient(await _fixture.SignInAsync(platformEmail, Password));

        var (_, client) = await OwnerAsync();

        // A platform action against another customer, which must not appear in
        // this customer's audit view.
        var otherCustomerId = await _fixture.SeedCustomerAsync();
        await platform.PostAsync($"/api/v1/customers/{otherCustomerId}/suspend", null);

        var visible = await (await client.GetAsync("/api/v1/audit-logs?pageSize=100")).Content.ReadAsStringAsync();

        Assert.DoesNotContain(otherCustomerId.ToString(), visible);
    }

    [Fact]
    public async Task ACustomerCannotReachPlatformOnlyOperations()
    {
        if (!_fixture.IsAvailable) return;

        var (_, client) = await OwnerAsync();

        // CustomerOwner holds no customer.create: creating customers is platform
        // business, not something a customer does for itself.
        var created = await client.PostAsJsonAsync("/api/v1/customers", new
        {
            name = "Another",
            contactEmail = $"new-{Guid.NewGuid():n}@example.test",
        });

        Assert.Equal(HttpStatusCode.Forbidden, created.StatusCode);
    }

    [Fact]
    public async Task ATokenForAnotherPrincipalTypeCannotReachTheDashboardApi()
    {
        if (!_fixture.IsAvailable) return;

        // Cross-principal access is refused at the policy layer: a token minted
        // for a different principal type is not a dashboard user
        // (docs/authentication.md section 4).
        //
        // The token is forged here rather than obtained from an issuer. Phase 8
        // removed the store-side issuer that used to mint these, but the rule it
        // was checking outlives it: a correctly signed token is still not a
        // dashboard session unless it says it is. Signing it with the host's own
        // key is the point — a test that presented an unsigned token would pass
        // for the wrong reason.
        var token = ForgeToken(principalType: "tenant_user");
        var client = _fixture.CreateClient(token);

        var response = await client.GetAsync("/api/v1/customers");

        Assert.True(
            response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden,
            $"Expected the request to be refused, got {response.StatusCode}.");
    }

    private static string ForgeToken(string principalType)
    {
        var key = new SymmetricSecurityKey(
            Encoding.UTF8.GetBytes(PostgresApiFixture.TestSigningKey));

        var token = new JwtSecurityToken(
            issuer: PostgresApiFixture.TestIssuer,
            audience: PostgresApiFixture.TestAudience,
            claims:
            [
                new Claim(JwtRegisteredClaimNames.Sub, Guid.NewGuid().ToString()),
                new Claim("principal_type", principalType),
            ],
            expires: DateTime.UtcNow.AddMinutes(5),
            signingCredentials: new SigningCredentials(key, SecurityAlgorithms.HmacSha256));

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    private sealed record PagedBody<T>(IReadOnlyCollection<T> Items, long TotalCount);

    private sealed record CustomerBody(Guid Id, string Name, string Status);

    private sealed record StoreBody(Guid Id, Guid CustomerId, string Status);
}
