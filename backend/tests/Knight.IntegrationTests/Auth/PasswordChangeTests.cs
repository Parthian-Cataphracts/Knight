using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Knight.Contracts.Auth;
using Knight.IntegrationTests.Infrastructure;
using Xunit;

namespace Knight.IntegrationTests.Auth;

[Collection(PostgresCollection.Name)]
public sealed class PasswordChangeTests
{
    private readonly PostgresApiFixture _fixture;

    public PasswordChangeTests(PostgresApiFixture fixture)
    {
        _fixture = fixture;
    }

    private async Task<(HttpClient Client, string AccessToken)> LoginAsync(string email, string password)
    {
        var client = _fixture.Factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/platform/auth/login", new LoginRequest { Email = email, Password = password });
        var body = await response.Content.ReadFromJsonAsync<LoginResponse>();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", body!.AccessToken);
        return (client, body.AccessToken);
    }

    [Fact]
    public async Task ChangePassword_WithCorrectCurrentPassword_Succeeds()
    {
        if (!_fixture.IsAvailable) return;

        var email = $"admin-{Guid.NewGuid():n}@example.test";
        await _fixture.SeedPlatformAdminAsync(email, "OldPassword1");
        var (client, _) = await LoginAsync(email, "OldPassword1");

        var response = await client.PostAsJsonAsync("/api/platform/auth/change-password", new ChangePasswordRequest
        {
            CurrentPassword = "OldPassword1",
            NewPassword = "BrandNewPassword1"
        });

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    [Fact]
    public async Task ChangePassword_WithWrongCurrentPassword_Rejected()
    {
        if (!_fixture.IsAvailable) return;

        var email = $"admin-{Guid.NewGuid():n}@example.test";
        await _fixture.SeedPlatformAdminAsync(email, "OldPassword1");
        var (client, _) = await LoginAsync(email, "OldPassword1");

        var response = await client.PostAsJsonAsync("/api/platform/auth/change-password", new ChangePasswordRequest
        {
            CurrentPassword = "WrongCurrentPassword",
            NewPassword = "BrandNewPassword1"
        });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task ChangePassword_NewPasswordThenAuthenticates_OldPasswordNoLongerDoes()
    {
        if (!_fixture.IsAvailable) return;

        var email = $"admin-{Guid.NewGuid():n}@example.test";
        await _fixture.SeedPlatformAdminAsync(email, "OldPassword1");
        var (client, _) = await LoginAsync(email, "OldPassword1");

        await client.PostAsJsonAsync("/api/platform/auth/change-password", new ChangePasswordRequest
        {
            CurrentPassword = "OldPassword1",
            NewPassword = "BrandNewPassword1"
        });

        var freshClient = _fixture.Factory.CreateClient();
        var oldPasswordLogin = await freshClient.PostAsJsonAsync("/api/platform/auth/login", new LoginRequest { Email = email, Password = "OldPassword1" });
        Assert.Equal(HttpStatusCode.Unauthorized, oldPasswordLogin.StatusCode);

        var newPasswordLogin = await freshClient.PostAsJsonAsync("/api/platform/auth/login", new LoginRequest { Email = email, Password = "BrandNewPassword1" });
        Assert.Equal(HttpStatusCode.OK, newPasswordLogin.StatusCode);
    }

    [Fact]
    public async Task ChangePassword_RevokesAllExistingRefreshSessions()
    {
        if (!_fixture.IsAvailable) return;

        var email = $"admin-{Guid.NewGuid():n}@example.test";
        await _fixture.SeedPlatformAdminAsync(email, "OldPassword1");
        var (client, _) = await LoginAsync(email, "OldPassword1");

        var otherDevice = _fixture.Factory.CreateClient();
        await otherDevice.PostAsJsonAsync("/api/platform/auth/login", new LoginRequest { Email = email, Password = "OldPassword1" });

        await client.PostAsJsonAsync("/api/platform/auth/change-password", new ChangePasswordRequest
        {
            CurrentPassword = "OldPassword1",
            NewPassword = "BrandNewPassword1"
        });

        var otherDeviceRefresh = await otherDevice.PostAsync("/api/platform/auth/refresh", null);

        Assert.Equal(HttpStatusCode.Unauthorized, otherDeviceRefresh.StatusCode);
    }

    [Fact]
    public async Task ChangePassword_RecordsAuditEvent()
    {
        if (!_fixture.IsAvailable) return;

        var email = $"admin-{Guid.NewGuid():n}@example.test";
        var adminId = await _fixture.SeedPlatformAdminAsync(email, "OldPassword1");
        var (client, _) = await LoginAsync(email, "OldPassword1");

        await client.PostAsJsonAsync("/api/platform/auth/change-password", new ChangePasswordRequest
        {
            CurrentPassword = "OldPassword1",
            NewPassword = "BrandNewPassword1"
        });

        var auditRecorded = await _fixture.WithScopeAsync(
            (context, _) => context.AuditLogEntries.AnyAsync(e => e.Action == "PasswordChanged" && e.ActorUserId == adminId),
            platformContext: true);

        Assert.True(auditRecorded);
    }
}
