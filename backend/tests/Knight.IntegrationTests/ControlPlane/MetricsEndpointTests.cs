using System.Net;
using Knight.IntegrationTests.Infrastructure;
using Microsoft.AspNetCore.Hosting;

namespace Knight.IntegrationTests.ControlPlane;

/// <summary>
/// The Prometheus scrape endpoint (`/metrics`) — the pull half of KNIGHT's
/// telemetry, off unless a deployment asks for it (docs/observability.md §4).
///
/// The properties worth holding are that it is not exposed by default — exposing
/// metrics is a deployment decision, and a surface that describes the platform's
/// shape must not appear on its own — and that when it is on it serves the
/// Prometheus exposition format carrying KNIGHT's own instruments, not only the
/// framework's.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class MetricsEndpointTests
{
    private readonly PostgresApiFixture _fixture;

    public MetricsEndpointTests(PostgresApiFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task ByDefault_TheScrapeEndpointIsNotExposed()
    {
        if (!_fixture.IsAvailable) return;

        // The shared fixture runs with telemetry off, which is the default.
        var client = _fixture.Factory.CreateClient();

        var response = await client.GetAsync("/metrics");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task WhenEnabled_ItServesPrometheusMetricsIncludingKnightsOwn()
    {
        if (!_fixture.IsAvailable) return;

        using var factory = _fixture.Factory.WithWebHostBuilder(builder =>
            builder.UseSetting("Telemetry:PrometheusEnabled", "true"));

        var client = factory.CreateClient();

        var response = await client.GetAsync("/metrics");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();

        // Prometheus exposition format: every series is introduced by a TYPE line.
        Assert.Contains("# TYPE", body);

        // One of KNIGHT's own observable gauges, not merely the framework's HTTP
        // metrics. Dots in the instrument name become underscores on the way out,
        // so `knight.incidents.open` is scraped as `knight_incidents_open`.
        Assert.Contains("knight_incidents_open", body);
    }
}
