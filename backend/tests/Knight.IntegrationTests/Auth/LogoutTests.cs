using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Knight.Contracts.Auth;
using Knight.IntegrationTests.Infrastructure;
using Xunit;

namespace Knight.IntegrationTests.Auth;

[Collection(PostgresCollection.Name)]
public sealed class LogoutTests
{
    private readonly PostgresApiFixture _fixture;

    public LogoutTests(PostgresApiFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Logout_RevokesCurrentSession_AndSubsequentRefreshFails()
    {
        if (!_fixture.IsAvailable) return;

        var email = $"admin-{Guid.NewGuid():n}@example.test";
        await _fixture.SeedPlatformAdminAsync(email, "AdminPassword1");
        var client = _fixture.Factory.CreateClient();

        await client.PostAsJsonAsync("/api/platform/auth/login", new LoginRequest { Email = email, Password = "AdminPassword1" });

        var logoutResponse = await client.PostAsync("/api/platform/auth/logout", null);
        Assert.Equal(HttpStatusCode.NoContent, logoutResponse.StatusCode);

        var refreshResponse = await client.PostAsync("/api/platform/auth/refresh", null);
        Assert.Equal(HttpStatusCode.Unauthorized, refreshResponse.StatusCode);
    }

    [Fact]
    public async Task Logout_ClearsRefreshCookie()
    {
        if (!_fixture.IsAvailable) return;

        var email = $"admin-{Guid.NewGuid():n}@example.test";
        await _fixture.SeedPlatformAdminAsync(email, "AdminPassword1");
        var client = _fixture.Factory.CreateClient();

        await client.PostAsJsonAsync("/api/platform/auth/login", new LoginRequest { Email = email, Password = "AdminPassword1" });
        var logoutResponse = await client.PostAsync("/api/platform/auth/logout", null);

        var setCookie = logoutResponse.Headers.TryGetValues("Set-Cookie", out var values) ? values.FirstOrDefault(v => v.Contains("platform-rt")) : null;
        Assert.NotNull(setCookie);
        Assert.Contains("01 Jan 1970", setCookie, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Logout_WhenCookieAlreadyAbsent_IsSafeAndIdempotent()
    {
        if (!_fixture.IsAvailable) return;

        var client = _fixture.Factory.CreateClient();

        var first = await client.PostAsync("/api/platform/auth/logout", null);
        var second = await client.PostAsync("/api/platform/auth/logout", null);

        Assert.Equal(HttpStatusCode.NoContent, first.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, second.StatusCode);
    }

    [Fact]
    public async Task LogoutAll_RevokesAllSessionsForCurrentPrincipal_ButNotAnotherUsers()
    {
        if (!_fixture.IsAvailable) return;

        var emailA = $"admin-a-{Guid.NewGuid():n}@example.test";
        var emailB = $"admin-b-{Guid.NewGuid():n}@example.test";
        await _fixture.SeedPlatformAdminAsync(emailA, "AdminAPassword1");
        await _fixture.SeedPlatformAdminAsync(emailB, "AdminBPassword1");

        // Admin A logs in from two "devices" (two separate clients/sessions).
        var deviceOne = _fixture.Factory.CreateClient();
        var deviceTwo = _fixture.Factory.CreateClient();
        var deviceOneLogin = await deviceOne.PostAsJsonAsync("/api/platform/auth/login", new LoginRequest { Email = emailA, Password = "AdminAPassword1" });
        await deviceTwo.PostAsJsonAsync("/api/platform/auth/login", new LoginRequest { Email = emailA, Password = "AdminAPassword1" });

        var otherAdminClient = _fixture.Factory.CreateClient();
        await otherAdminClient.PostAsJsonAsync("/api/platform/auth/login", new LoginRequest { Email = emailB, Password = "AdminBPassword1" });

        // logout-all via deviceOne, authenticated with its own access token.
        var deviceOneAccessToken = (await deviceOneLogin.Content.ReadFromJsonAsync<LoginResponse>())!.AccessToken;
        deviceOne.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", deviceOneAccessToken);
        var logoutAllResponse = await deviceOne.PostAsync("/api/platform/auth/logout-all", null);
        Assert.Equal(HttpStatusCode.NoContent, logoutAllResponse.StatusCode);

        var deviceOneRefresh = await deviceOne.PostAsync("/api/platform/auth/refresh", null);
        var deviceTwoRefresh = await deviceTwo.PostAsync("/api/platform/auth/refresh", null);
        Assert.Equal(HttpStatusCode.Unauthorized, deviceOneRefresh.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, deviceTwoRefresh.StatusCode);

        var otherAdminRefresh = await otherAdminClient.PostAsync("/api/platform/auth/refresh", null);
        Assert.Equal(HttpStatusCode.OK, otherAdminRefresh.StatusCode);
    }
}
