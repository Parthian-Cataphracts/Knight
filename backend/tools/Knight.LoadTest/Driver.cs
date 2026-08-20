using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;

namespace Knight.LoadTest;

/// <summary>
/// Drives ingestion traffic at the running API and reports what came back.
///
/// Latency is reported as percentiles, not as an average. An average hides
/// exactly the thing this measurement exists to find: a p99 that has walked off
/// while the mean stayed flat is what a store actually experiences when the
/// control plane is struggling.
///
/// Non-2xx responses are counted separately and by status, and 429 is counted
/// apart from the rest. A rate limiter doing its job is a healthy result, not an
/// error, but a run that was mostly rate-limited measured the limiter rather
/// than the write path — and that difference has to be visible in the output
/// rather than averaged into a throughput number.
/// </summary>
internal static class Driver
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static async Task<int> RunAsync(string[] args)
    {
        var baseUrl = Arguments.Value(args, "--base-url", "http://localhost:5215").TrimEnd('/');
        var seconds = Arguments.Number(args, "--duration", 30);
        var concurrency = Arguments.Number(args, "--concurrency", 16);
        var fixturePath = Arguments.FixturePath(args);

        if (!File.Exists(fixturePath))
        {
            Console.Error.WriteLine($"No fixture file at {fixturePath}. Run `seed` first.");
            return 1;
        }

        var fixtures = JsonSerializer.Deserialize<List<StoreFixture>>(
            await File.ReadAllTextAsync(fixturePath), Json) ?? [];

        if (fixtures.Count == 0)
        {
            Console.Error.WriteLine("The fixture file names no stores.");
            return 1;
        }

        Console.WriteLine($"Target      {baseUrl}");
        Console.WriteLine($"Stores      {fixtures.Count}");
        Console.WriteLine($"Concurrency {concurrency}");
        Console.WriteLine($"Duration    {seconds}s");
        Console.WriteLine();

        using var http = new HttpClient
        {
            // Long enough that a slow request is recorded as slow rather than as
            // a failure, which is the number being looked for.
            Timeout = TimeSpan.FromSeconds(30),
        };

        Console.WriteLine("Handshaking ...");
        var sessions = new List<Session>();

        foreach (var fixture in fixtures)
        {
            var token = await HandshakeAsync(http, baseUrl, fixture);
            if (token is null)
            {
                Console.Error.WriteLine($"  {fixture.Slug}: handshake refused.");
                continue;
            }

            sessions.Add(new Session(fixture, token));
        }

        if (sessions.Count == 0)
        {
            Console.Error.WriteLine("No store could authenticate. Nothing to measure.");
            return 1;
        }

        Console.WriteLine($"  {sessions.Count} of {fixtures.Count} authenticated.");
        Console.WriteLine();

        var results = new Results();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
        var started = Stopwatch.GetTimestamp();

        var workers = Enumerable.Range(0, concurrency)
            .Select(worker => WorkAsync(http, baseUrl, sessions, worker, results, deadline.Token))
            .ToArray();

        await Task.WhenAll(workers);

        var elapsed = Stopwatch.GetElapsedTime(started);
        results.Report(elapsed);
        return 0;
    }

    private static async Task<string?> HandshakeAsync(HttpClient http, string baseUrl, StoreFixture fixture)
    {
        var response = await http.PostAsJsonAsync($"{baseUrl}/api/v1/ingest/handshake", new
        {
            clientId = fixture.ClientId,
            clientSecret = fixture.ClientSecret,
            environment = fixture.Environment,
            storeVersion = "2.0.0",
            runtime = "load-test",

            // A nonce per handshake, because a replayed one is refused.
            nonce = Guid.NewGuid().ToString("N"),
        }, Json);

        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Json);
        return body.TryGetProperty("accessToken", out var token) ? token.GetString() : null;
    }

    private static async Task WorkAsync(
        HttpClient http,
        string baseUrl,
        IReadOnlyList<Session> sessions,
        int worker,
        Results results,
        CancellationToken cancellationToken)
    {
        // Seeded per worker so a run is varied but repeatable in shape.
        var random = new Random(worker * 7919);

        while (!cancellationToken.IsCancellationRequested)
        {
            var session = sessions[random.Next(sessions.Count)];

            // The mix a real store produces: heartbeats are periodic and
            // constant, events and logs come in bursts, errors are rarer. Sending
            // one endpoint at full rate would measure that endpoint rather than
            // the system.
            var roll = random.Next(100);
            var (path, payload, kind) = roll switch
            {
                < 40 => ($"{baseUrl}/api/v1/ingest/heartbeat", Heartbeat(), "heartbeat"),
                < 70 => ($"{baseUrl}/api/v1/ingest/events", Events(random), "events"),
                < 90 => ($"{baseUrl}/api/v1/ingest/logs", Logs(random), "logs"),
                _ => ($"{baseUrl}/api/v1/ingest/errors", Errors(random), "errors"),
            };

            var request = new HttpRequestMessage(HttpMethod.Post, path)
            {
                Content = JsonContent.Create(payload, options: Json),
            };
            request.Headers.Authorization = new("Bearer", session.Token);

            var started = Stopwatch.GetTimestamp();

            try
            {
                using var response = await http.SendAsync(request, cancellationToken);
                results.Record(kind, (int)response.StatusCode, Stopwatch.GetElapsedTime(started));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // The clock ran out mid-request. Not a failure of the server.
                return;
            }
            catch (Exception)
            {
                results.Record(kind, 0, Stopwatch.GetElapsedTime(started));
            }
        }
    }

    private static object Heartbeat() => new
    {
        environment = Seeder.Environment,
        status = "Healthy",
        storeVersion = "2.0.0",
        dependencies = new { database = "healthy", cache = "healthy" },
        features = Array.Empty<object>(),
        detail = (string?)null,
    };

    private static object Events(Random random) => new
    {
        environment = Seeder.Environment,
        version = "2.0.0",
        events = Enumerable.Range(0, random.Next(1, 6)).Select(_ => new
        {
            type = "order.placed",
            occurredAt = DateTimeOffset.UtcNow,
            summary = "An order was placed",
            payload = new Dictionary<string, object>
            {
                ["orderId"] = Guid.NewGuid().ToString(),
                ["total"] = random.Next(10_000, 900_000),
            },
        }).ToArray(),
    };

    private static object Logs(Random random) => new
    {
        environment = Seeder.Environment,
        version = "2.0.0",
        entries = Enumerable.Range(0, random.Next(1, 21)).Select(_ => new
        {
            level = random.Next(10) < 8 ? "Information" : "Warning",
            message = "Checkout completed",
            timestamp = DateTimeOffset.UtcNow,
            service = "apps.orders",
        }).ToArray(),
    };

    private static object Errors(Random random) => new
    {
        environment = Seeder.Environment,
        version = "2.0.0",
        events = new[]
        {
            new
            {
                // A handful of distinct types, so grouping does real work: every
                // error being identical would measure the dedupe path and nothing
                // else.
                exceptionType = $"ValueError{random.Next(5)}",
                message = "Something went wrong in checkout",
                stackTrace = "File \"/app/apps/orders/views.py\", line 42, in place_order",
                occurredAt = DateTimeOffset.UtcNow,
                endpoint = "/checkout",
                httpMethod = "POST",
                statusCode = 500,
            },
        },
    };

    private sealed record Session(StoreFixture Fixture, string Token);
}
