using System.Net;
using System.Net.Http.Json;
using Knight.Contracts.Auth;
using Knight.IntegrationTests.Infrastructure;
using Tenancy.Domain;
using Xunit;

namespace Knight.IntegrationTests.Auth;

[Collection(PostgresCollection.Name)]
public sealed class TenantLoginTests
{
    private readonly PostgresApiFixture _fixture;

    public TenantLoginTests(PostgresApiFixture fixture)
    {
        _fixture = fixture;
    }

    private HttpClient CreateClientForHost(string host)
    {
        var client = _fixture.Factory.CreateClient();
        client.BaseAddress = new Uri($"http://{host}");
        return client;
    }

    [Fact]
    public async Task Login_OnCorrectTenantHost_Succeeds()
    {
        if (!_fixture.IsAvailable) return;

        var email = $"user-{Guid.NewGuid():n}@example.test";
        var (_, host, _) = await _fixture.SeedActiveTenantWithUserAsync(email, "TenantUserPass1");

        var client = CreateClientForHost(host);
        var response = await client.PostAsJsonAsync("/api/tenant/auth/login", new LoginRequest { Email = email, Password = "TenantUserPass1" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Login_WithWrongPassword_Fails()
    {
        if (!_fixture.IsAvailable) return;

        var email = $"user-{Guid.NewGuid():n}@example.test";
        var (_, host, _) = await _fixture.SeedActiveTenantWithUserAsync(email, "TenantUserPass1");

        var client = CreateClientForHost(host);
        var response = await client.PostAsJsonAsync("/api/tenant/auth/login", new LoginRequest { Email = email, Password = "WrongPassword1" });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Login_WithUnknownEmail_FailsGenerically()
    {
        if (!_fixture.IsAvailable) return;

        var (_, host, _) = await _fixture.SeedActiveTenantWithUserAsync($"seed-{Guid.NewGuid():n}@example.test", "SeedPass1");

        var client = CreateClientForHost(host);
        var response = await client.PostAsJsonAsync("/api/tenant/auth/login", new LoginRequest
        {
            Email = $"unknown-{Guid.NewGuid():n}@example.test",
            Password = "WhateverPass1"
        });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Login_AsPlatformAdmin_CannotAuthenticateThroughTenantLogin()
    {
        if (!_fixture.IsAvailable) return;

        var email = $"admin-{Guid.NewGuid():n}@example.test";
        await _fixture.SeedPlatformAdminAsync(email, "AdminPassword1");
        var (_, host, _) = await _fixture.SeedActiveTenantWithUserAsync($"seed-{Guid.NewGuid():n}@example.test", "SeedPass1");

        var client = CreateClientForHost(host);
        var response = await client.PostAsJsonAsync("/api/tenant/auth/login", new LoginRequest { Email = email, Password = "AdminPassword1" });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Login_SameEmailInTwoTenants_OnlyResolvesWithinCurrentTenant()
    {
        if (!_fixture.IsAvailable) return;

        const string sharedEmail = "shared.staff@example.test";
        var (_, hostA, _) = await _fixture.SeedActiveTenantWithUserAsync(sharedEmail, "TenantAPassword1");
        var (_, hostB, _) = await _fixture.SeedActiveTenantWithUserAsync(sharedEmail, "TenantBPassword1");

        var clientA = CreateClientForHost(hostA);
        var responseAWithBPassword = await clientA.PostAsJsonAsync("/api/tenant/auth/login", new LoginRequest { Email = sharedEmail, Password = "TenantBPassword1" });
        Assert.Equal(HttpStatusCode.Unauthorized, responseAWithBPassword.StatusCode);

        var responseAWithOwnPassword = await clientA.PostAsJsonAsync("/api/tenant/auth/login", new LoginRequest { Email = sharedEmail, Password = "TenantAPassword1" });
        Assert.Equal(HttpStatusCode.OK, responseAWithOwnPassword.StatusCode);

        var clientB = CreateClientForHost(hostB);
        var responseBWithOwnPassword = await clientB.PostAsJsonAsync("/api/tenant/auth/login", new LoginRequest { Email = sharedEmail, Password = "TenantBPassword1" });
        Assert.Equal(HttpStatusCode.OK, responseBWithOwnPassword.StatusCode);
    }

    [Fact]
    public async Task Login_WithSuspendedTenant_IsRejected()
    {
        if (!_fixture.IsAvailable) return;

        var email = $"user-{Guid.NewGuid():n}@example.test";
        var (tenantId, host, _) = await _fixture.SeedActiveTenantWithUserAsync(email, "TenantUserPass1");
        await _fixture.SetTenantStatusAsync(tenantId, t => t.Suspend(DateTimeOffset.UtcNow));

        var client = CreateClientForHost(host);
        var response = await client.PostAsJsonAsync("/api/tenant/auth/login", new LoginRequest { Email = email, Password = "TenantUserPass1" });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Login_WithArchivedTenant_IsRejected()
    {
        if (!_fixture.IsAvailable) return;

        var email = $"user-{Guid.NewGuid():n}@example.test";
        var (tenantId, host, _) = await _fixture.SeedActiveTenantWithUserAsync(email, "TenantUserPass1");
        await _fixture.SetTenantStatusAsync(tenantId, t => t.Archive(DateTimeOffset.UtcNow));

        var client = CreateClientForHost(host);
        var response = await client.PostAsJsonAsync("/api/tenant/auth/login", new LoginRequest { Email = email, Password = "TenantUserPass1" });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }
}
