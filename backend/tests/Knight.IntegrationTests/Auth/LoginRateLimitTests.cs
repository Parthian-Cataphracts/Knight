using System.Net;
using System.Net.Http.Json;
using Knight.Contracts.Auth;
using Knight.IntegrationTests.Infrastructure;
using Xunit;

namespace Knight.IntegrationTests.Auth;

[Collection(PostgresCollection.Name)]
public sealed class LoginRateLimitTests
{
    private readonly PostgresApiFixture _fixture;

    public LoginRateLimitTests(PostgresApiFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task PlatformLogin_ExceedingLimit_Returns429()
    {
        if (!_fixture.IsAvailable) return;

        // A dedicated low-limit variant of the host, isolated from the shared
        // fixture's generous test-wide override (see PostgresApiFixture) —
        // this is the only test that needs to actually observe a 429.
        await using var lowLimitFactory = _fixture.Factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("RateLimiting:PlatformLoginPermitLimit", "3");
        });

        var client = lowLimitFactory.CreateClient();
        var request = new LoginRequest { Email = "rate-limit-probe@example.test", Password = "irrelevant" };

        HttpResponseMessage? last = null;
        for (var i = 0; i < 10; i++)
        {
            last = await client.PostAsJsonAsync("/api/platform/auth/login", request);
            if (last.StatusCode == HttpStatusCode.TooManyRequests)
            {
                break;
            }
        }

        Assert.Equal(HttpStatusCode.TooManyRequests, last!.StatusCode);
    }
}
