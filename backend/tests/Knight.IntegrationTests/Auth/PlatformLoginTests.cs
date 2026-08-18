using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Knight.Contracts.Auth;
using Knight.IntegrationTests.Infrastructure;
using Xunit;

namespace Knight.IntegrationTests.Auth;

[Collection(PostgresCollection.Name)]
public sealed class PlatformLoginTests
{
    private readonly PostgresApiFixture _fixture;

    public PlatformLoginTests(PostgresApiFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Login_WithValidCredentials_Succeeds()
    {
        if (!_fixture.IsAvailable) return;

        var email = $"admin-{Guid.NewGuid():n}@example.test";
        await _fixture.SeedPlatformAdminAsync(email, "CorrectHorseBattery1");

        var client = _fixture.Factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/platform/auth/login", new LoginRequest { Email = email, Password = "CorrectHorseBattery1" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<LoginResponse>();
        Assert.NotNull(body);
        Assert.False(string.IsNullOrEmpty(body!.AccessToken));
        Assert.True(response.Headers.TryGetValues("Set-Cookie", out _));
    }

    [Fact]
    public async Task Login_WithWrongPassword_Fails()
    {
        if (!_fixture.IsAvailable) return;

        var email = $"admin-{Guid.NewGuid():n}@example.test";
        await _fixture.SeedPlatformAdminAsync(email, "CorrectHorseBattery1");

        var client = _fixture.Factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/platform/auth/login", new LoginRequest { Email = email, Password = "WrongPassword1" });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Login_WithUnknownEmail_FailsGenerically()
    {
        if (!_fixture.IsAvailable) return;

        var client = _fixture.Factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/platform/auth/login", new LoginRequest
        {
            Email = $"unknown-{Guid.NewGuid():n}@example.test",
            Password = "WhateverPassword1"
        });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Login_AsTenantUser_CannotAuthenticateThroughPlatformLogin()
    {
        if (!_fixture.IsAvailable) return;

        var email = $"user-{Guid.NewGuid():n}@example.test";
        var (_, _, _) = await _fixture.SeedActiveTenantWithUserAsync(email, "TenantUserPass1");

        var client = _fixture.Factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/platform/auth/login", new LoginRequest { Email = email, Password = "TenantUserPass1" });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Login_WithDisabledAdmin_Fails()
    {
        if (!_fixture.IsAvailable) return;

        var email = $"admin-{Guid.NewGuid():n}@example.test";
        var adminId = await _fixture.SeedPlatformAdminAsync(email, "CorrectHorseBattery1");
        await _fixture.WithScopeAsync(async (context, _) =>
        {
            var admin = await context.PlatformAdmins.FirstAsync(a => a.Id == adminId);
            admin.Disable(DateTimeOffset.UtcNow);
            await context.SaveChangesAsync();
        }, platformContext: true);

        var client = _fixture.Factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/platform/auth/login", new LoginRequest { Email = email, Password = "CorrectHorseBattery1" });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Login_WithLockedAdmin_Fails()
    {
        if (!_fixture.IsAvailable) return;

        var email = $"admin-{Guid.NewGuid():n}@example.test";
        await _fixture.SeedPlatformAdminAsync(email, "CorrectHorseBattery1");

        var client = _fixture.Factory.CreateClient();

        // Default lockout threshold is 5 — exhaust it with wrong passwords.
        for (var i = 0; i < 5; i++)
        {
            await client.PostAsJsonAsync("/api/platform/auth/login", new LoginRequest { Email = email, Password = "WrongPassword1" });
        }

        var response = await client.PostAsJsonAsync("/api/platform/auth/login", new LoginRequest { Email = email, Password = "CorrectHorseBattery1" });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
