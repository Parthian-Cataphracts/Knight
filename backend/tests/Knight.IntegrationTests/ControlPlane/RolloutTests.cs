using System.Net;
using System.Net.Http.Json;
using AccessControl.Domain;
using Knight.IntegrationTests.Infrastructure;

namespace Knight.IntegrationTests.ControlPlane;

/// <summary>
/// The HTTP surface of staged rollouts.
///
/// The sequencing rules themselves are unit-tested against the aggregate, where
/// every branch can be reached without a fleet. What can only be checked here is
/// the part that protects other people's production systems: who is allowed to
/// start one at all.
///
/// That check is release-blocking. A rollout crosses customers and installs code
/// into stores its caller does not own, so an authorisation mistake on these
/// routes is the difference between a platform operation and a customer being
/// able to push code into a neighbour's shop.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class RolloutTests
{
    private const string Password = "correct horse battery staple";

    private readonly PostgresApiFixture _fixture;

    public RolloutTests(PostgresApiFixture fixture)
    {
        _fixture = fixture;
    }

    private static string Email() => $"user-{Guid.NewGuid():n}@knight.test";

    private async Task<HttpClient> ClientAsync(string role = SystemRoles.SuperAdmin, Guid? customerId = null)
    {
        var email = Email();
        await _fixture.SeedUserAsync(email, Password, role, customerId);
        return _fixture.CreateClient(await _fixture.SignInAsync(email, Password));
    }

    private static object PlanBody(string slug = "advanced-promotions", string version = "1.1.0") => new
    {
        slug,
        version,
        wavePercentages = new[] { 50, 100 },
        failureThreshold = 1,
    };

    [Fact]
    public async Task ACustomerUserCannotSeeOrStartARollout()
    {
        if (!_fixture.IsAvailable) return;

        var customerId = await _fixture.SeedCustomerAsync();
        var client = await ClientAsync(SystemRoles.CustomerOwner, customerId);

        // Not 404 and not an empty list: a customer must be refused outright.
        // Rolling a version across the fleet is not a scoped-down version of
        // anything a customer may do.
        var list = await client.GetAsync("/api/v1/rollouts");
        Assert.Equal(HttpStatusCode.Forbidden, list.StatusCode);

        var plan = await client.PostAsJsonAsync("/api/v1/rollouts", PlanBody());
        Assert.Equal(HttpStatusCode.Forbidden, plan.StatusCode);
    }

    [Fact]
    public async Task APlatformRoleWithoutFeaturePublishCannotStartARollout()
    {
        if (!_fixture.IsAvailable) return;

        // Support is platform staff and can read a great deal. Sending code to
        // every store is still not theirs to do: rolling out a version is the
        // same weight of decision as publishing one, and carries the same
        // permission.
        var client = await ClientAsync(SystemRoles.Support);

        var plan = await client.PostAsJsonAsync("/api/v1/rollouts", PlanBody());
        Assert.Equal(HttpStatusCode.Forbidden, plan.StatusCode);
    }

    [Fact]
    public async Task PlanningARolloutWithNothingToRollOutIsRefusedWithAReason()
    {
        if (!_fixture.IsAvailable) return;

        var client = await ClientAsync();

        // No store has this Feature installed, so there is no fleet to move. The
        // useful answer is a refusal that says so, not a rollout of zero stores
        // that reports success having done nothing.
        var response = await client.PostAsJsonAsync("/api/v1/rollouts", PlanBody(slug: $"absent-{Guid.NewGuid():n}"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("nothing to roll out", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AnUnknownRolloutIsNotFound()
    {
        if (!_fixture.IsAvailable) return;

        var client = await ClientAsync();

        var response = await client.GetAsync($"/api/v1/rollouts/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task ASuperAdminCanListRollouts()
    {
        if (!_fixture.IsAvailable) return;

        var client = await ClientAsync();

        var response = await client.GetAsync("/api/v1/rollouts");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var page = await response.Content.ReadFromJsonAsync<RolloutPageBody>();
        Assert.NotNull(page);
        Assert.NotNull(page!.Items);
    }

    private sealed record RolloutPageBody(RolloutBody[] Items, int Page, int PageSize, long TotalCount);

    private sealed record RolloutBody(Guid Id, string FeatureSlug, string TargetVersion, string State, int TotalStores);
}
