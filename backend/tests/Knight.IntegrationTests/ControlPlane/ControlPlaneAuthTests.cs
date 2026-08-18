using System.Net;
using System.Net.Http.Json;
using AccessControl.Domain;
using Knight.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Knight.IntegrationTests.ControlPlane;

[Collection(PostgresCollection.Name)]
public sealed class ControlPlaneAuthTests
{
    private const string Password = "correct horse battery staple";

    private readonly PostgresApiFixture _fixture;

    public ControlPlaneAuthTests(PostgresApiFixture fixture)
    {
        _fixture = fixture;
    }

    private static string Email() => $"user-{Guid.NewGuid():n}@knight.test";

    [Fact]
    public async Task Login_WithValidCredentials_ReturnsTokensAndPermissions()
    {
        if (!_fixture.IsAvailable) return;

        var email = Email();
        await _fixture.SeedUserAsync(email, Password, SystemRoles.Developer);

        var client = _fixture.Factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/v1/auth/login", new { email, password = Password });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<LoginBody>();
        Assert.Equal("succeeded", body!.Status);
        Assert.False(string.IsNullOrWhiteSpace(body.AccessToken));
        Assert.False(string.IsNullOrWhiteSpace(body.RefreshToken));
        Assert.Contains("monitoring.view", body.User!.Permissions);
        Assert.Contains(SystemRoles.Developer, body.User.Roles);
    }

    [Fact]
    public async Task Login_WithWrongPassword_IsIndistinguishableFromAnUnknownAccount()
    {
        if (!_fixture.IsAvailable) return;

        var email = Email();
        await _fixture.SeedUserAsync(email, Password, SystemRoles.Developer);

        var client = _fixture.Factory.CreateClient();

        var wrongPassword = await client.PostAsJsonAsync("/api/v1/auth/login", new { email, password = "wrong" });
        var unknownAccount = await client.PostAsJsonAsync("/api/v1/auth/login", new { email = Email(), password = Password });

        // Same status and same body: nothing in the response tells a caller
        // whether the account exists (docs/authentication.md section 1).
        Assert.Equal(HttpStatusCode.Unauthorized, wrongPassword.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, unknownAccount.StatusCode);

        var wrongPasswordBody = await wrongPassword.Content.ReadAsStringAsync();
        Assert.DoesNotContain(email, wrongPasswordBody);
        Assert.Contains("unauthorized", wrongPasswordBody);
        Assert.Contains("unauthorized", await unknownAccount.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task RepeatedFailures_LockTheAccountEvenWithTheRightPassword()
    {
        if (!_fixture.IsAvailable) return;

        var email = Email();
        var userId = await _fixture.SeedUserAsync(email, Password, SystemRoles.Developer);
        var client = _fixture.Factory.CreateClient();

        for (var attempt = 0; attempt < 5; attempt++)
        {
            await client.PostAsJsonAsync("/api/v1/auth/login", new { email, password = "wrong" });
        }

        var response = await client.PostAsJsonAsync("/api/v1/auth/login", new { email, password = Password });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);

        var lockedUntil = await _fixture.WithControlPlaneScopeAsync(async (context, _) =>
            await context.Users.IgnoreQueryFilters().Where(u => u.Id == userId).Select(u => u.LockedUntil).FirstAsync());

        Assert.NotNull(lockedUntil);
    }

    [Fact]
    public async Task Refresh_RotatesTheTokenAndInvalidatesThePreviousOne()
    {
        if (!_fixture.IsAvailable) return;

        var email = Email();
        await _fixture.SeedUserAsync(email, Password, SystemRoles.Developer);

        var client = _fixture.Factory.CreateClient();
        var login = await (await client.PostAsJsonAsync("/api/v1/auth/login", new { email, password = Password }))
            .Content.ReadFromJsonAsync<LoginBody>();

        var refreshed = await client.PostAsJsonAsync("/api/v1/auth/refresh", new { refreshToken = login!.RefreshToken });
        var refreshedBody = await refreshed.Content.ReadFromJsonAsync<LoginBody>();

        Assert.Equal(HttpStatusCode.OK, refreshed.StatusCode);
        Assert.NotEqual(login.RefreshToken, refreshedBody!.RefreshToken);

        var replayed = await client.PostAsJsonAsync("/api/v1/auth/refresh", new { refreshToken = login.RefreshToken });
        Assert.Equal(HttpStatusCode.Unauthorized, replayed.StatusCode);
    }

    [Fact]
    public async Task ReplayingAConsumedRefreshToken_RevokesTheWholeFamily()
    {
        if (!_fixture.IsAvailable) return;

        var email = Email();
        await _fixture.SeedUserAsync(email, Password, SystemRoles.Developer);

        var client = _fixture.Factory.CreateClient();
        var login = await (await client.PostAsJsonAsync("/api/v1/auth/login", new { email, password = Password }))
            .Content.ReadFromJsonAsync<LoginBody>();

        var rotated = await (await client.PostAsJsonAsync("/api/v1/auth/refresh", new { refreshToken = login!.RefreshToken }))
            .Content.ReadFromJsonAsync<LoginBody>();

        // Presenting the consumed token looks like a stolen one, so the current
        // token is revoked too rather than left working for the thief's victim.
        await client.PostAsJsonAsync("/api/v1/auth/refresh", new { refreshToken = login.RefreshToken });

        var afterReuse = await client.PostAsJsonAsync("/api/v1/auth/refresh", new { refreshToken = rotated!.RefreshToken });
        Assert.Equal(HttpStatusCode.Unauthorized, afterReuse.StatusCode);
    }

    [Fact]
    public async Task Logout_EndsTheLoginRatherThanTheCurrentLeg()
    {
        if (!_fixture.IsAvailable) return;

        var email = Email();
        await _fixture.SeedUserAsync(email, Password, SystemRoles.Developer);

        var client = _fixture.Factory.CreateClient();
        var login = await (await client.PostAsJsonAsync("/api/v1/auth/login", new { email, password = Password }))
            .Content.ReadFromJsonAsync<LoginBody>();

        await client.PostAsJsonAsync("/api/v1/auth/logout", new { refreshToken = login!.RefreshToken });

        var afterLogout = await client.PostAsJsonAsync("/api/v1/auth/refresh", new { refreshToken = login.RefreshToken });
        Assert.Equal(HttpStatusCode.Unauthorized, afterLogout.StatusCode);
    }

    [Fact]
    public async Task Me_ReturnsTheResolvedPermissions()
    {
        if (!_fixture.IsAvailable) return;

        var email = Email();
        var customerId = await _fixture.SeedCustomerAsync();
        await _fixture.SeedUserAsync(email, Password, SystemRoles.CustomerOwner, customerId);

        var client = _fixture.CreateClient(await _fixture.SignInAsync(email, Password));
        var response = await client.GetAsync("/api/v1/auth/me");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<UserBody>();
        Assert.Equal(customerId, body!.CustomerId);
        Assert.Contains("store.manage", body.Permissions);
        Assert.DoesNotContain("feature.publish", body.Permissions);
    }

    [Fact]
    public async Task Me_WithoutAToken_IsUnauthorized()
    {
        if (!_fixture.IsAvailable) return;

        var response = await _fixture.Factory.CreateClient().GetAsync("/api/v1/auth/me");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task AnAccountWithMfaEnabled_MustSupplyACode()
    {
        if (!_fixture.IsAvailable) return;

        var email = Email();
        var userId = await _fixture.SeedUserAsync(email, Password, SystemRoles.Admin);
        var client = _fixture.Factory.CreateClient();

        var withoutCode = await (await client.PostAsJsonAsync("/api/v1/auth/login", new { email, password = Password }))
            .Content.ReadFromJsonAsync<LoginBody>();

        Assert.Equal("mfa_required", withoutCode!.Status);
        Assert.Null(withoutCode.AccessToken);

        var withCode = await (await client.PostAsJsonAsync(
                "/api/v1/auth/login",
                new { email, password = Password, mfaCode = await _fixture.CurrentCodeAsync(userId) }))
            .Content.ReadFromJsonAsync<LoginBody>();

        Assert.Equal("succeeded", withCode!.Status);
        Assert.False(string.IsNullOrWhiteSpace(withCode.AccessToken));
    }

    [Fact]
    public async Task AWrongMfaCodeIsRejected()
    {
        if (!_fixture.IsAvailable) return;

        var email = Email();
        await _fixture.SeedUserAsync(email, Password, SystemRoles.Admin);
        var client = _fixture.Factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/v1/auth/login", new { email, password = Password, mfaCode = "000000" });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task APrivilegedAccountWithoutMfa_CanReachEnrolmentAndNothingElse()
    {
        if (!_fixture.IsAvailable) return;

        var email = Email();
        await _fixture.SeedUserAsync(email, Password, SystemRoles.Admin, enrolMfa: false);

        var client = _fixture.Factory.CreateClient();
        var login = await (await client.PostAsJsonAsync("/api/v1/auth/login", new { email, password = Password }))
            .Content.ReadFromJsonAsync<LoginBody>();

        Assert.Equal("mfa_enrollment_required", login!.Status);

        var authenticated = _fixture.CreateClient(login.AccessToken!);

        // Holds Admin, which carries customer.view — but the second factor is
        // still outstanding, so no permission is satisfied yet.
        Assert.Equal(HttpStatusCode.Forbidden, (await authenticated.GetAsync("/api/v1/customers")).StatusCode);

        var enrolment = await authenticated.PostAsync("/api/v1/auth/mfa/enroll", null);
        Assert.Equal(HttpStatusCode.OK, enrolment.StatusCode);
    }

    [Fact]
    public async Task ConfirmingMfa_UnlocksTheSession()
    {
        if (!_fixture.IsAvailable) return;

        var email = Email();
        var userId = await _fixture.SeedUserAsync(email, Password, SystemRoles.Admin, enrolMfa: false);

        var client = _fixture.Factory.CreateClient();
        var login = await (await client.PostAsJsonAsync("/api/v1/auth/login", new { email, password = Password }))
            .Content.ReadFromJsonAsync<LoginBody>();

        var authenticated = _fixture.CreateClient(login!.AccessToken!);
        await authenticated.PostAsync("/api/v1/auth/mfa/enroll", null);

        var confirmed = await authenticated.PostAsJsonAsync(
            "/api/v1/auth/mfa/confirm",
            new { code = await _fixture.CurrentCodeAsync(userId) });

        Assert.Equal(HttpStatusCode.OK, confirmed.StatusCode);

        var body = await confirmed.Content.ReadFromJsonAsync<LoginBody>();
        var upgraded = _fixture.CreateClient(body!.AccessToken!);

        Assert.Equal(HttpStatusCode.OK, (await upgraded.GetAsync("/api/v1/customers")).StatusCode);
    }

    private sealed record LoginBody(string Status, string? AccessToken, string? RefreshToken, UserBody? User);

    private sealed record UserBody(
        Guid Id,
        string Email,
        Guid? CustomerId,
        IReadOnlyCollection<string> Roles,
        IReadOnlyCollection<string> Permissions,
        bool MfaEnabled,
        bool MfaSatisfied);
}
