using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AccessControl.Domain;
using Ingestion.Domain;
using Knight.IntegrationTests.Infrastructure;

namespace Knight.IntegrationTests.ControlPlane;

/// <summary>
/// Reading the central log stream: narrowing it by severity so the errors,
/// warnings and alerts stand apart from the noise, by a full-text search and a
/// time range, and exporting the same filter as CSV (docs/risks.md §3.4).
///
/// Driven against a real database, because the filtering is SQL — a severity is a
/// set of raw tokens, the search is a case-insensitive LIKE with its wildcards
/// escaped, and none of that is exercised by an in-memory list.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class LogStreamTests
{
    private const string Password = "correct horse battery staple";

    private readonly PostgresApiFixture _fixture;

    public LogStreamTests(PostgresApiFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task MinimumSeverity_SeparatesTheProblemsFromTheNoise()
    {
        if (!_fixture.IsAvailable) return;

        var customerId = await _fixture.SeedCustomerAsync();
        var storeId = await _fixture.SeedStoreAsync(customerId);
        await SeedLogsAsync(customerId, storeId,
        [
            ("DEBUG", "a debug line", Base),
            ("INFO", "an info line", Base),
            ("WARN", "a warning line", Base),
            ("ERROR", "an error line", Base),
            ("CRITICAL", "an alert line", Base),
        ]);

        var client = await ClientAsync();

        var levels = await LevelsAsync(client, $"/api/v1/logs?storeId={storeId}&minSeverity=Warning");

        // Warning and above only: three of the five.
        Assert.Equal(["Critical", "Error", "Warning"], levels.OrderBy(l => l));
    }

    [Fact]
    public async Task Search_MatchesTheMessageCaseInsensitively_AndTreatsWildcardsLiterally()
    {
        if (!_fixture.IsAvailable) return;

        var customerId = await _fixture.SeedCustomerAsync();
        var storeId = await _fixture.SeedStoreAsync(customerId);
        await SeedLogsAsync(customerId, storeId,
        [
            ("INFO", "Checkout completed for order 5182", Base),
            ("INFO", "Payment declined at 50% capacity", Base),
            ("INFO", "Cache warmed", Base),
        ]);

        var client = await ClientAsync();

        // Case-insensitive substring.
        var checkout = await MessagesAsync(client, $"/api/v1/logs?storeId={storeId}&search=CHECKOUT");
        Assert.Single(checkout);
        Assert.Contains("Checkout completed", checkout[0]);

        // The '%' is a literal, not a wildcard: it matches only the line that has one.
        var percent = await MessagesAsync(client, $"/api/v1/logs?storeId={storeId}&search=50%25");
        Assert.Single(percent);
        Assert.Contains("50% capacity", percent[0]);
    }

    [Fact]
    public async Task TimeRange_KeepsOnlyTheEntriesInside()
    {
        if (!_fixture.IsAvailable) return;

        var customerId = await _fixture.SeedCustomerAsync();
        var storeId = await _fixture.SeedStoreAsync(customerId);

        var old = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var mid = new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);
        var recent = new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);

        await SeedLogsAsync(customerId, storeId,
        [
            ("INFO", "old line", old),
            ("INFO", "mid line", mid),
            ("INFO", "recent line", recent),
        ]);

        var client = await ClientAsync();

        var messages = await MessagesAsync(
            client,
            $"/api/v1/logs?storeId={storeId}&from=2026-05-01T00:00:00Z&to=2026-07-01T00:00:00Z");

        Assert.Single(messages);
        Assert.Contains("mid line", messages[0]);
    }

    [Fact]
    public async Task Export_ReturnsTheSameFilterAsCsv()
    {
        if (!_fixture.IsAvailable) return;

        var customerId = await _fixture.SeedCustomerAsync();
        var storeId = await _fixture.SeedStoreAsync(customerId);
        await SeedLogsAsync(customerId, storeId,
        [
            ("INFO", "kept info", Base),
            ("ERROR", "kept error", Base),
        ]);

        var client = await ClientAsync();

        var response = await client.GetAsync($"/api/v1/logs/export?storeId={storeId}&minSeverity=Error");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/csv", response.Content.Headers.ContentType?.MediaType);

        var csv = await response.Content.ReadAsStringAsync();
        var lines = csv.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        // Header plus the one error line; the info line is below the floor.
        Assert.Equal("Timestamp,Level,Service,Store,Environment,TraceId,Message", lines[0]);
        Assert.Equal(2, lines.Length);
        Assert.Contains("kept error", lines[1]);
    }

    [Fact]
    public async Task Export_NeedsItsOwnPermission_ThatViewingDoesNotGrant()
    {
        if (!_fixture.IsAvailable) return;

        var customerId = await _fixture.SeedCustomerAsync();
        var storeId = await _fixture.SeedStoreAsync(customerId);

        // Support may read the stream but not bulk-export it — export is a
        // data-egress action of the least-redacted data, kept to a permission of
        // its own that Support does not hold.
        var support = await ClientAsync(SystemRoles.Support);

        Assert.Equal(HttpStatusCode.OK, (await support.GetAsync($"/api/v1/logs?storeId={storeId}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await support.GetAsync($"/api/v1/logs/export?storeId={storeId}")).StatusCode);
    }

    private static readonly DateTimeOffset Base = new(2026, 8, 20, 12, 0, 0, TimeSpan.Zero);

    private async Task SeedLogsAsync(
        Guid customerId,
        Guid storeId,
        IReadOnlyList<(string Level, string Message, DateTimeOffset Timestamp)> entries)
    {
        await _fixture.WithControlPlaneScopeAsync(async (context, _) =>
        {
            foreach (var (level, message, timestamp) in entries)
            {
                context.StoreLogEntries.Add(StoreLogEntry.Record(
                    Guid.NewGuid(),
                    storeId,
                    customerId,
                    timestamp,
                    timestamp,
                    level,
                    "Production",
                    message));
            }

            await context.SaveChangesAsync();
        });
    }

    private async Task<HttpClient> ClientAsync(string role = SystemRoles.SuperAdmin)
    {
        var email = $"reader-{Guid.NewGuid():n}@example.test";
        await _fixture.SeedUserAsync(email, Password, role);
        var token = await _fixture.SignInAsync(email, Password);
        return _fixture.CreateClient(token);
    }

    private static async Task<string[]> LevelsAsync(HttpClient client, string url)
    {
        var body = await ReadAsync(client, url);
        return body.GetProperty("items").EnumerateArray()
            .Select(item => item.GetProperty("level").GetString()!)
            .ToArray();
    }

    private static async Task<string[]> MessagesAsync(HttpClient client, string url)
    {
        var body = await ReadAsync(client, url);
        return body.GetProperty("items").EnumerateArray()
            .Select(item => item.GetProperty("message").GetString()!)
            .ToArray();
    }

    private static async Task<JsonElement> ReadAsync(HttpClient client, string url)
    {
        var response = await client.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var document = await response.Content.ReadFromJsonAsync<JsonDocument>();
        return document!.RootElement.Clone();
    }
}
