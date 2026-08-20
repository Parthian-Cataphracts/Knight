using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AccessControl.Domain;
using Knight.IntegrationTests.Infrastructure;

namespace Knight.IntegrationTests.ControlPlane;

/// <summary>
/// Administering somebody else's account.
///
/// The cases that matter are the ones about a credential's blast radius: a
/// one-time password exists in readable form for exactly one response, an
/// administrator can never read an existing one, and every reset is audited —
/// because resetting is also what somebody who has stolen an administrator's
/// session would do to take an account over.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class AccountAdministrationTests
{
    private const string Password = "correct horse battery staple";

    private readonly PostgresApiFixture _fixture;

    public AccountAdministrationTests(PostgresApiFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task AnAccountIsCreatedWithAOneTimePasswordReturnedExactlyOnce()
    {
        if (!_fixture.IsAvailable) return;

        var client = await PlatformClientAsync(SystemRoles.SuperAdmin);
        var email = Email();

        var created = await client.PostAsJsonAsync("/api/v1/users", new
        {
            email,
            displayName = "New Operator",
            roleIds = Array.Empty<Guid>(),
        });

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        var body = JsonDocument.Parse(await created.Content.ReadAsStringAsync()).RootElement;
        var temporary = body.GetProperty("temporaryPassword").GetString();

        Assert.False(string.IsNullOrWhiteSpace(temporary));

        var id = body.GetProperty("account").GetProperty("id").GetGuid();

        // Reading the account back never yields the password: it is stored only
        // as a hash, and no endpoint exposes it.
        //
        // Searched by email rather than read off a large first page: the suite
        // creates accounts freely, and a paged listing that happened to push this
        // one onto page two would fail the assertion below for a reason that has
        // nothing to do with what is being tested.
        var listed = await client.GetAsync($"/api/v1/users?q={Uri.EscapeDataString(email)}");
        listed.EnsureSuccessStatusCode();

        var raw = await listed.Content.ReadAsStringAsync();

        Assert.DoesNotContain(temporary!, raw, StringComparison.Ordinal);
        Assert.Contains(id.ToString(), raw, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TheCreatedAccountCanSignInWithThatPasswordAndNoOther()
    {
        if (!_fixture.IsAvailable) return;

        var client = await PlatformClientAsync(SystemRoles.SuperAdmin);
        var email = Email();

        var created = await client.PostAsJsonAsync("/api/v1/users", new
        {
            email,
            displayName = "New Operator",
            roleIds = Array.Empty<Guid>(),
        });

        created.EnsureSuccessStatusCode();

        var temporary = JsonDocument.Parse(await created.Content.ReadAsStringAsync())
            .RootElement.GetProperty("temporaryPassword").GetString()!;

        var anonymous = _fixture.Factory.CreateClient();

        var wrong = await anonymous.PostAsJsonAsync("/api/v1/auth/login", new
        {
            email,
            password = "not-the-generated-one",
        });

        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);

        var right = await anonymous.PostAsJsonAsync("/api/v1/auth/login", new { email, password = temporary });

        Assert.Equal(HttpStatusCode.OK, right.StatusCode);
    }

    [Fact]
    public async Task ResettingAPasswordIssuesANewOneAndInvalidatesTheOld()
    {
        if (!_fixture.IsAvailable) return;

        var client = await PlatformClientAsync(SystemRoles.SuperAdmin);
        var email = Email();

        var created = await client.PostAsJsonAsync("/api/v1/users", new
        {
            email,
            displayName = "Operator",
            roleIds = Array.Empty<Guid>(),
        });

        created.EnsureSuccessStatusCode();

        var body = JsonDocument.Parse(await created.Content.ReadAsStringAsync()).RootElement;
        var first = body.GetProperty("temporaryPassword").GetString()!;
        var id = body.GetProperty("account").GetProperty("id").GetGuid();

        var reset = await client.PostAsync($"/api/v1/users/{id}/password/reset", null);
        reset.EnsureSuccessStatusCode();

        var second = JsonDocument.Parse(await reset.Content.ReadAsStringAsync())
            .RootElement.GetProperty("temporaryPassword").GetString()!;

        Assert.NotEqual(first, second);

        var anonymous = _fixture.Factory.CreateClient();

        var withOld = await anonymous.PostAsJsonAsync("/api/v1/auth/login", new { email, password = first });
        Assert.Equal(HttpStatusCode.Unauthorized, withOld.StatusCode);

        var withNew = await anonymous.PostAsJsonAsync("/api/v1/auth/login", new { email, password = second });
        Assert.Equal(HttpStatusCode.OK, withNew.StatusCode);
    }

    [Fact]
    public async Task ASuspendedAccountCannotSignIn()
    {
        if (!_fixture.IsAvailable) return;

        var client = await PlatformClientAsync(SystemRoles.SuperAdmin);
        var email = Email();

        var created = await client.PostAsJsonAsync("/api/v1/users", new
        {
            email,
            displayName = "Operator",
            roleIds = Array.Empty<Guid>(),
        });

        created.EnsureSuccessStatusCode();

        var body = JsonDocument.Parse(await created.Content.ReadAsStringAsync()).RootElement;
        var temporary = body.GetProperty("temporaryPassword").GetString()!;
        var id = body.GetProperty("account").GetProperty("id").GetGuid();

        var suspended = await client.PostAsync($"/api/v1/users/{id}/suspend", null);
        suspended.EnsureSuccessStatusCode();

        var anonymous = _fixture.Factory.CreateClient();
        var attempt = await anonymous.PostAsJsonAsync("/api/v1/auth/login", new { email, password = temporary });

        Assert.Equal(HttpStatusCode.Unauthorized, attempt.StatusCode);

        // Reactivating restores it, so suspension is a state rather than a
        // deletion.
        await client.PostAsync($"/api/v1/users/{id}/activate", null);

        var second = await anonymous.PostAsJsonAsync("/api/v1/auth/login", new { email, password = temporary });

        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
    }

    [Fact]
    public async Task ACustomerAccountCannotBeGivenAPlatformRole()
    {
        if (!_fixture.IsAvailable) return;

        // The rule that keeps the isolation model intact: an account scoped to
        // one customer must never hold a permission that spans all of them.
        var client = await PlatformClientAsync(SystemRoles.SuperAdmin);
        var customerId = await _fixture.SeedCustomerAsync();

        var roles = await client.GetAsync("/api/v1/roles");
        roles.EnsureSuccessStatusCode();

        var platformRole = JsonDocument.Parse(await roles.Content.ReadAsStringAsync())
            .RootElement.GetProperty("items")
            .EnumerateArray()
            .First(role => role.GetProperty("scope").GetString() == "Platform");

        var refused = await client.PostAsJsonAsync("/api/v1/users", new
        {
            email = Email(),
            displayName = "Customer staff",
            customerId,
            roleIds = new[] { platformRole.GetProperty("id").GetGuid() },
        });

        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
    }

    [Fact]
    public async Task ADuplicateEmailIsRefused()
    {
        if (!_fixture.IsAvailable) return;

        var client = await PlatformClientAsync(SystemRoles.SuperAdmin);
        var email = Email();

        var first = await client.PostAsJsonAsync("/api/v1/users", new
        {
            email,
            displayName = "First",
            roleIds = Array.Empty<Guid>(),
        });

        first.EnsureSuccessStatusCode();

        var second = await client.PostAsJsonAsync("/api/v1/users", new
        {
            email,
            displayName = "Second",
            roleIds = Array.Empty<Guid>(),
        });

        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
    }

    [Fact]
    public async Task ManagingAccounts_RequiresThePermission()
    {
        if (!_fixture.IsAvailable) return;

        // Support can see the access screen but not change who may do what.
        var support = await PlatformClientAsync(SystemRoles.Support);

        var refused = await support.PostAsJsonAsync("/api/v1/users", new
        {
            email = Email(),
            displayName = "Nope",
            roleIds = Array.Empty<Guid>(),
        });

        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
    }

    [Fact]
    public async Task EveryAdministrativeActionIsAudited()
    {
        if (!_fixture.IsAvailable) return;

        var client = await PlatformClientAsync(SystemRoles.SuperAdmin);

        var created = await client.PostAsJsonAsync("/api/v1/users", new
        {
            email = Email(),
            displayName = "Audited",
            roleIds = Array.Empty<Guid>(),
        });

        created.EnsureSuccessStatusCode();

        var id = JsonDocument.Parse(await created.Content.ReadAsStringAsync())
            .RootElement.GetProperty("account").GetProperty("id").GetGuid();

        await client.PostAsync($"/api/v1/users/{id}/password/reset", null);

        var audit = await client.GetAsync("/api/v1/audit-logs?pageSize=100");
        audit.EnsureSuccessStatusCode();

        var actions = JsonDocument.Parse(await audit.Content.ReadAsStringAsync())
            .RootElement.GetProperty("items")
            .EnumerateArray()
            .Select(entry => entry.GetProperty("action").GetString())
            .ToArray();

        Assert.Contains("user.created", actions);
        Assert.Contains("user.password.reset", actions);
    }

    [Fact]
    public async Task ThePermissionCatalogueIsServed()
    {
        if (!_fixture.IsAvailable) return;

        // The role editor offers real keys rather than a free-text box that
        // silently accepts a typo.
        var client = await PlatformClientAsync(SystemRoles.SuperAdmin);

        var response = await client.GetAsync("/api/v1/roles/permissions");
        response.EnsureSuccessStatusCode();

        var permissions = JsonDocument.Parse(await response.Content.ReadAsStringAsync())
            .RootElement.GetProperty("items")
            .EnumerateArray()
            .Select(item => item.GetString())
            .ToArray();

        Assert.Contains("customer.view", permissions);
        Assert.Contains("installation.manage", permissions);
    }

    private static string Email() => $"user-{Guid.NewGuid():n}@knight.test";

    private async Task<HttpClient> PlatformClientAsync(string role)
    {
        var email = Email();
        await _fixture.SeedUserAsync(email, Password, role);

        return _fixture.CreateClient(await _fixture.SignInAsync(email, Password));
    }
}
