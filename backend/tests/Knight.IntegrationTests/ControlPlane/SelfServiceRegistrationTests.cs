using System.Net;
using System.Net.Http.Json;
using Knight.IntegrationTests.Infrastructure;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Onboarding;

namespace Knight.IntegrationTests.ControlPlane;

/// <summary>
/// Public self-service sign-up, end to end (docs/self-service-saas-plan.md §12,
/// phase B). The properties that matter are the ones that are easy to get wrong:
/// a fresh account cannot sign in until it verifies, registering a taken address
/// is silent and creates nothing, and a self-service account is bound to its own
/// customer like every other account.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class SelfServiceRegistrationTests
{
    private const string Password = "correct horse battery staple";

    private readonly PostgresApiFixture _fixture;

    public SelfServiceRegistrationTests(PostgresApiFixture fixture)
    {
        _fixture = fixture;
    }

    private static string Email() => $"owner-{Guid.NewGuid():n}@knight.test";

    /// <summary>A verification sender that keeps the links instead of mailing them, so a test can follow one.</summary>
    private sealed class CapturingVerificationSender : IVerificationEmailSender
    {
        private readonly List<string> _sent = [];
        private readonly object _gate = new();

        public bool CanSend => true;

        public IReadOnlyList<string> Sent
        {
            get { lock (_gate) { return _sent.ToArray(); } }
        }

        public Task<bool> SendAsync(string email, string displayName, string verificationToken, CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                _sent.Add(verificationToken);
            }

            return Task.FromResult(true);
        }
    }

    private (HttpClient Client, CapturingVerificationSender Sender) NewClient()
    {
        var sender = new CapturingVerificationSender();
        var factory = _fixture.Factory.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IVerificationEmailSender>();
                services.AddSingleton<IVerificationEmailSender>(sender);
            }));

        return (factory.CreateClient(), sender);
    }

    [Fact]
    public async Task RegisteringThenVerifyingLetsTheAccountSignIn()
    {
        if (!_fixture.IsAvailable) return;

        var (client, sender) = NewClient();
        var email = Email();

        var registered = await client.PostAsJsonAsync("/api/v1/auth/register", new { email, password = Password, name = "Owner" });
        Assert.Equal(HttpStatusCode.Accepted, registered.StatusCode);

        // Unverified: the account exists but the door is shut.
        var beforeVerify = await client.PostAsJsonAsync("/api/v1/auth/login", new { email, password = Password });
        Assert.Equal(HttpStatusCode.Unauthorized, beforeVerify.StatusCode);

        var token = Assert.Single(sender.Sent);
        var verified = await client.PostAsJsonAsync("/api/v1/auth/verify-email", new { token });
        Assert.Equal(HttpStatusCode.OK, verified.StatusCode);

        // Verified: the same credentials now sign in.
        var afterVerify = await client.PostAsJsonAsync("/api/v1/auth/login", new { email, password = Password });
        Assert.Equal(HttpStatusCode.OK, afterVerify.StatusCode);
        var body = await afterVerify.Content.ReadFromJsonAsync<LoginBody>();
        Assert.Equal("succeeded", body!.Status);
        Assert.False(string.IsNullOrWhiteSpace(body.AccessToken));

        // Bound to a customer of its own, and holding the owner role.
        Assert.NotNull(body.User!.CustomerId);
        Assert.Contains("CustomerOwner", body.User.Roles);
    }

    [Fact]
    public async Task RegisteringATakenEmailIsSilentAndCreatesNoSecondAccount()
    {
        if (!_fixture.IsAvailable) return;

        var (client, sender) = NewClient();
        var email = Email();

        var first = await client.PostAsJsonAsync("/api/v1/auth/register", new { email, password = Password, name = "Owner" });
        var second = await client.PostAsJsonAsync("/api/v1/auth/register", new { email, password = Password, name = "Impostor" });

        // Same answer both times: nothing tells the caller the second was a repeat.
        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, second.StatusCode);

        // The second created nothing and sent nothing.
        Assert.Single(sender.Sent);

        var accounts = await _fixture.WithControlPlaneScopeAsync(async (context, _) =>
            await context.Users.IgnoreQueryFilters().CountAsync(u => u.NormalizedEmail == email.ToUpperInvariant()));
        Assert.Equal(1, accounts);
    }

    [Fact]
    public async Task VerifyingWithAGarbageTokenIsRejected()
    {
        if (!_fixture.IsAvailable) return;

        var (client, _) = NewClient();

        var response = await client.PostAsJsonAsync("/api/v1/auth/verify-email", new { token = "not-a-real-token" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task ResendingReissuesTheTokenAndRetiresThePreviousOne()
    {
        if (!_fixture.IsAvailable) return;

        var (client, sender) = NewClient();
        var email = Email();

        await client.PostAsJsonAsync("/api/v1/auth/register", new { email, password = Password, name = "Owner" });
        await client.PostAsJsonAsync("/api/v1/auth/resend-verification", new { email });

        Assert.Equal(2, sender.Sent.Count);
        var (first, second) = (sender.Sent[0], sender.Sent[1]);
        Assert.NotEqual(first, second);

        // The reissue retired the first link.
        var withOld = await client.PostAsJsonAsync("/api/v1/auth/verify-email", new { token = first });
        Assert.Equal(HttpStatusCode.BadRequest, withOld.StatusCode);

        var withNew = await client.PostAsJsonAsync("/api/v1/auth/verify-email", new { token = second });
        Assert.Equal(HttpStatusCode.OK, withNew.StatusCode);
    }

    [Fact]
    public async Task ResendingForAnUnknownEmailIsSilent()
    {
        if (!_fixture.IsAvailable) return;

        var (client, sender) = NewClient();

        var response = await client.PostAsJsonAsync("/api/v1/auth/resend-verification", new { email = Email() });

        // Accepted like any other resend, and nothing was actually sent.
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Empty(sender.Sent);
    }

    private sealed record LoginBody(string Status, string? AccessToken, UserBody? User);

    private sealed record UserBody(Guid Id, string Email, Guid? CustomerId, IReadOnlyCollection<string> Roles);
}
