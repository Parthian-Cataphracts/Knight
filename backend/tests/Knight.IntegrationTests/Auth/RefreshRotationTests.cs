using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Knight.Contracts.Auth;
using Knight.IntegrationTests.Infrastructure;
using Xunit;

namespace Knight.IntegrationTests.Auth;

[Collection(PostgresCollection.Name)]
public sealed class RefreshRotationTests
{
    private readonly PostgresApiFixture _fixture;

    public RefreshRotationTests(PostgresApiFixture fixture)
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
    public async Task PlatformRefresh_WithValidCookie_RotatesAndSucceeds()
    {
        if (!_fixture.IsAvailable) return;

        var email = $"admin-{Guid.NewGuid():n}@example.test";
        await _fixture.SeedPlatformAdminAsync(email, "AdminPassword1");
        var client = _fixture.Factory.CreateClient();

        var loginResponse = await client.PostAsJsonAsync("/api/platform/auth/login", new LoginRequest { Email = email, Password = "AdminPassword1" });
        var originalCookie = CookieTestHelpers.ExtractCookieValue(loginResponse, "platform-rt");
        Assert.NotNull(originalCookie);

        var refreshResponse = await client.PostAsync("/api/platform/auth/refresh", null);
        Assert.Equal(HttpStatusCode.OK, refreshResponse.StatusCode);

        var rotatedCookie = CookieTestHelpers.ExtractCookieValue(refreshResponse, "platform-rt");
        Assert.NotNull(rotatedCookie);
        Assert.NotEqual(originalCookie, rotatedCookie);
    }

    [Fact]
    public async Task PlatformRefresh_OldTokenAfterRotation_CannotBeUsedAgain()
    {
        if (!_fixture.IsAvailable) return;

        var email = $"admin-{Guid.NewGuid():n}@example.test";
        await _fixture.SeedPlatformAdminAsync(email, "AdminPassword1");
        var client = _fixture.Factory.CreateClient();

        var loginResponse = await client.PostAsJsonAsync("/api/platform/auth/login", new LoginRequest { Email = email, Password = "AdminPassword1" });
        var originalCookie = CookieTestHelpers.ExtractCookieValue(loginResponse, "platform-rt")!;

        // Legitimate rotation via the client's own cookie jar.
        var firstRefresh = await client.PostAsync("/api/platform/auth/refresh", null);
        Assert.Equal(HttpStatusCode.OK, firstRefresh.StatusCode);

        // Replay the pre-rotation token explicitly, via a fresh client with no
        // cookie jar of its own — otherwise its auto-attached (already-rotated)
        // cookie would ride along next to our manually attached one.
        using var replayRequest = new HttpRequestMessage(HttpMethod.Post, "/api/platform/auth/refresh");
        CookieTestHelpers.AttachCookie(replayRequest, "platform-rt", originalCookie);
        var replayResponse = await _fixture.Factory.CreateClient().SendAsync(replayRequest);

        Assert.Equal(HttpStatusCode.Unauthorized, replayResponse.StatusCode);
    }

    [Fact]
    public async Task PlatformRefresh_NewRotatedToken_CanRefreshAgain()
    {
        if (!_fixture.IsAvailable) return;

        var email = $"admin-{Guid.NewGuid():n}@example.test";
        await _fixture.SeedPlatformAdminAsync(email, "AdminPassword1");
        var client = _fixture.Factory.CreateClient();

        await client.PostAsJsonAsync("/api/platform/auth/login", new LoginRequest { Email = email, Password = "AdminPassword1" });
        var firstRefresh = await client.PostAsync("/api/platform/auth/refresh", null);
        Assert.Equal(HttpStatusCode.OK, firstRefresh.StatusCode);

        var secondRefresh = await client.PostAsync("/api/platform/auth/refresh", null);

        Assert.Equal(HttpStatusCode.OK, secondRefresh.StatusCode);
    }

    [Fact]
    public async Task TenantRefresh_UsingPlatformCookiePath_NeverReachesTenantRefresh()
    {
        if (!_fixture.IsAvailable) return;

        // Platform and tenant refresh cookies are scoped to different Paths, so
        // a platform cookie is never even sent to /api/tenant/auth/refresh by a
        // real browser. This proves the tenant refresh endpoint rejects a raw
        // platform refresh token even if explicitly attached.
        var adminEmail = $"admin-{Guid.NewGuid():n}@example.test";
        await _fixture.SeedPlatformAdminAsync(adminEmail, "AdminPassword1");
        var (_, host, _) = await _fixture.SeedActiveTenantWithUserAsync($"user-{Guid.NewGuid():n}@example.test", "TenantUserPass1");

        var platformClient = _fixture.Factory.CreateClient();
        var loginResponse = await platformClient.PostAsJsonAsync("/api/platform/auth/login", new LoginRequest { Email = adminEmail, Password = "AdminPassword1" });
        var platformCookie = CookieTestHelpers.ExtractCookieValue(loginResponse, "platform-rt")!;

        var tenantClient = CreateClientForHost(host);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/tenant/auth/refresh");
        CookieTestHelpers.AttachCookie(request, "tenant-rt", platformCookie);
        var response = await tenantClient.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task PlatformRefresh_UsingTenantCookieValue_IsRejected()
    {
        if (!_fixture.IsAvailable) return;

        var email = $"realuser-{Guid.NewGuid():n}@example.test";
        var (_, host, _) = await _fixture.SeedActiveTenantWithUserAsync(email, "RealTenantPass1");
        var tenantClient = CreateClientForHost(host);
        var loginResponse = await tenantClient.PostAsJsonAsync("/api/tenant/auth/login", new LoginRequest { Email = email, Password = "RealTenantPass1" });
        var tenantCookie = CookieTestHelpers.ExtractCookieValue(loginResponse, "tenant-rt")!;

        var platformClient = _fixture.Factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/platform/auth/refresh");
        CookieTestHelpers.AttachCookie(request, "platform-rt", tenantCookie);
        var response = await platformClient.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task TenantRefresh_TenantATokenOnTenantBHost_IsRejected()
    {
        if (!_fixture.IsAvailable) return;

        var emailA = $"usera-{Guid.NewGuid():n}@example.test";
        var (_, hostA, _) = await _fixture.SeedActiveTenantWithUserAsync(emailA, "TenantAPass1");
        var (_, hostB, _) = await _fixture.SeedActiveTenantWithUserAsync($"userb-{Guid.NewGuid():n}@example.test", "TenantBPass1");

        var clientA = CreateClientForHost(hostA);
        var loginA = await clientA.PostAsJsonAsync("/api/tenant/auth/login", new LoginRequest { Email = emailA, Password = "TenantAPass1" });
        var tokenA = CookieTestHelpers.ExtractCookieValue(loginA, "tenant-rt")!;

        var clientB = CreateClientForHost(hostB);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/tenant/auth/refresh");
        CookieTestHelpers.AttachCookie(request, "tenant-rt", tokenA);
        var response = await clientB.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task PlatformRefresh_AfterAdminDisabled_Fails()
    {
        if (!_fixture.IsAvailable) return;

        var email = $"admin-{Guid.NewGuid():n}@example.test";
        var adminId = await _fixture.SeedPlatformAdminAsync(email, "AdminPassword1");
        var client = _fixture.Factory.CreateClient();
        await client.PostAsJsonAsync("/api/platform/auth/login", new LoginRequest { Email = email, Password = "AdminPassword1" });

        await _fixture.WithScopeAsync(async (context, _) =>
        {
            var admin = await context.PlatformAdmins.FirstAsync(a => a.Id == adminId);
            admin.Disable(DateTimeOffset.UtcNow);
            await context.SaveChangesAsync();
        }, platformContext: true);

        var refreshResponse = await client.PostAsync("/api/platform/auth/refresh", null);

        Assert.Equal(HttpStatusCode.Unauthorized, refreshResponse.StatusCode);
    }

    [Fact]
    public async Task TenantRefresh_AfterTenantSuspended_Fails()
    {
        if (!_fixture.IsAvailable) return;

        var email = $"user-{Guid.NewGuid():n}@example.test";
        var (tenantId, host, _) = await _fixture.SeedActiveTenantWithUserAsync(email, "TenantUserPass1");
        var client = CreateClientForHost(host);
        await client.PostAsJsonAsync("/api/tenant/auth/login", new LoginRequest { Email = email, Password = "TenantUserPass1" });

        await _fixture.SetTenantStatusAsync(tenantId, t => t.Suspend(DateTimeOffset.UtcNow));

        var refreshResponse = await client.PostAsync("/api/tenant/auth/refresh", null);

        Assert.Equal(HttpStatusCode.Forbidden, refreshResponse.StatusCode);
    }

    [Fact]
    public async Task TenantRefresh_AfterTenantArchived_Fails()
    {
        if (!_fixture.IsAvailable) return;

        var email = $"user-{Guid.NewGuid():n}@example.test";
        var (tenantId, host, _) = await _fixture.SeedActiveTenantWithUserAsync(email, "TenantUserPass1");
        var client = CreateClientForHost(host);
        await client.PostAsJsonAsync("/api/tenant/auth/login", new LoginRequest { Email = email, Password = "TenantUserPass1" });

        await _fixture.SetTenantStatusAsync(tenantId, t => t.Archive(DateTimeOffset.UtcNow));

        var refreshResponse = await client.PostAsync("/api/tenant/auth/refresh", null);

        Assert.Equal(HttpStatusCode.Forbidden, refreshResponse.StatusCode);
    }
}
