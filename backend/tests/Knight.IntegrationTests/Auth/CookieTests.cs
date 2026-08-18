using System.Net.Http.Json;
using Knight.Contracts.Auth;
using Knight.IntegrationTests.Infrastructure;
using Xunit;

namespace Knight.IntegrationTests.Auth;

[Collection(PostgresCollection.Name)]
public sealed class CookieTests
{
    private readonly PostgresApiFixture _fixture;

    public CookieTests(PostgresApiFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task PlatformLogin_SetsCookie_WithExpectedName_HttpOnly_Path()
    {
        if (!_fixture.IsAvailable) return;

        var email = $"admin-{Guid.NewGuid():n}@example.test";
        await _fixture.SeedPlatformAdminAsync(email, "AdminPassword1");
        var client = _fixture.Factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/platform/auth/login", new LoginRequest { Email = email, Password = "AdminPassword1" });
        var setCookie = response.Headers.GetValues("Set-Cookie").First(v => v.Contains("platform-rt"));

        Assert.Contains("platform-rt=", setCookie, StringComparison.Ordinal);
        Assert.Contains("httponly", setCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("path=/api/platform/auth", setCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite", setCookie, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TenantLogin_SetsCookie_WithDistinctNameAndPathFromPlatform()
    {
        if (!_fixture.IsAvailable) return;

        var email = $"user-{Guid.NewGuid():n}@example.test";
        var (_, host, _) = await _fixture.SeedActiveTenantWithUserAsync(email, "TenantUserPass1");
        var client = _fixture.Factory.CreateClient();
        client.BaseAddress = new Uri($"http://{host}");

        var response = await client.PostAsJsonAsync("/api/tenant/auth/login", new LoginRequest { Email = email, Password = "TenantUserPass1" });
        var setCookie = response.Headers.GetValues("Set-Cookie").First(v => v.Contains("tenant-rt"));

        Assert.Contains("tenant-rt=", setCookie, StringComparison.Ordinal);
        Assert.Contains("path=/api/tenant/auth", setCookie, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("platform-rt", setCookie, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Login_NeverReturnsRawRefreshTokenInJsonBody()
    {
        if (!_fixture.IsAvailable) return;

        var email = $"admin-{Guid.NewGuid():n}@example.test";
        await _fixture.SeedPlatformAdminAsync(email, "AdminPassword1");
        var client = _fixture.Factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/platform/auth/login", new LoginRequest { Email = email, Password = "AdminPassword1" });
        var raw = await response.Content.ReadAsStringAsync();

        Assert.DoesNotContain("refreshToken", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("rawToken", raw, StringComparison.OrdinalIgnoreCase);
    }
}
