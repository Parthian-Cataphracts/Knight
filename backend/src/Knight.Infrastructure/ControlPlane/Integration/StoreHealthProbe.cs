using System.Diagnostics;
using System.Text.Json;
using Knight.Application.Abstractions.ControlPlane;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Knight.Infrastructure.ControlPlane.Integration;

/// <summary>
/// Resolves where a store's integration surface lives.
///
/// The answer comes from the store's registered domain, or from an
/// operator-configured override — never from anything the store itself said.
/// A store that could nominate its own callback address could point KNIGHT at
/// any host it liked.
/// </summary>
internal sealed class StoreEndpointResolver
{
    private readonly StoreProbeOptions _options;

    public StoreEndpointResolver(IOptions<StoreProbeOptions> options)
    {
        _options = options.Value;
    }

    public Uri BaseUrl(string domain)
    {
        if (_options.BaseUrlOverrides.TryGetValue(domain, out var configured)
            && Uri.TryCreate(configured, UriKind.Absolute, out var overridden))
        {
            return overridden;
        }

        return new Uri($"{_options.Scheme}://{domain}");
    }

    public Uri Resolve(string domain, string path) => new(BaseUrl(domain), path);
}

/// <summary>
/// Asks a store how it is (docs/store-integration.md §5).
///
/// Retries are bounded and backed off, and every outcome — including "it never
/// answered" — is a result rather than an exception, because an unreachable
/// store is an ordinary state of the world that the caller must record, not an
/// error in KNIGHT. Nothing the store returns is trusted: the payload is read
/// under a size cap and every field is optional, so a store answering with
/// garbage produces an unhealthy observation rather than a broken poller.
/// </summary>
internal sealed class StoreHealthProbe : IStoreHealthProbe
{
    private readonly IHttpClientFactory _clients;
    private readonly StoreEndpointResolver _endpoints;
    private readonly ILogger<StoreHealthProbe> _logger;
    private readonly StoreProbeOptions _options;

    public StoreHealthProbe(
        IHttpClientFactory clients,
        StoreEndpointResolver endpoints,
        ILogger<StoreHealthProbe> logger,
        IOptions<StoreProbeOptions> options)
    {
        _clients = clients;
        _endpoints = endpoints;
        _logger = logger;
        _options = options.Value;
    }

    public async Task<StoreProbeResult> ProbeAsync(string domain, CancellationToken cancellationToken)
    {
        var url = _endpoints.Resolve(domain, _options.HealthPath);
        var client = _clients.CreateClient(StoreOutboundHttp.ClientName);

        string? lastFailure = null;

        for (var attempt = 1; attempt <= _options.MaxAttempts; attempt++)
        {
            var stopwatch = Stopwatch.StartNew();

            try
            {
                using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                var body = await ReadCappedAsync(response, cancellationToken);
                stopwatch.Stop();

                var latency = (int)stopwatch.ElapsedMilliseconds;

                if (!response.IsSuccessStatusCode)
                {
                    // A store that answers 503 is reachable and unhealthy, which
                    // is a different fact from one that never answered — and one
                    // more attempt will not change it.
                    return new StoreProbeResult(
                        nameof(StoreHealthOutcome.Unhealthy),
                        latency,
                        null,
                        null,
                        null,
                        null,
                        $"HTTP {(int)response.StatusCode}");
                }

                return Parse(body, latency);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or IOException)
            {
                stopwatch.Stop();
                lastFailure = Summarise(exception);

                _logger.LogDebug(
                    "Health probe attempt {Attempt}/{MaxAttempts} for {Domain} failed: {Reason}",
                    attempt,
                    _options.MaxAttempts,
                    domain,
                    lastFailure);

                if (attempt < _options.MaxAttempts)
                {
                    await DelayAsync(attempt, cancellationToken);
                }
            }
        }

        return new StoreProbeResult(nameof(StoreHealthOutcome.Unreachable), null, null, null, null, null, lastFailure);
    }

    /// <summary>
    /// Exponential backoff with jitter. The jitter matters more than the curve:
    /// without it, a KNIGHT restart re-polls every store in lockstep and a shared
    /// host sees every one of its stores probed in the same millisecond.
    /// </summary>
    private Task DelayAsync(int attempt, CancellationToken cancellationToken)
    {
        var baseDelay = _options.BackoffMilliseconds * Math.Pow(2, attempt - 1);
        var jitter = Random.Shared.NextDouble() * _options.BackoffMilliseconds;

        return Task.Delay(TimeSpan.FromMilliseconds(baseDelay + jitter), cancellationToken);
    }

    private static async Task<string> ReadCappedAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var buffer = new byte[StoreOutboundHttp.MaxResponseBytes];
        var read = 0;

        while (read < buffer.Length)
        {
            var chunk = await stream.ReadAsync(buffer.AsMemory(read), cancellationToken);
            if (chunk == 0)
            {
                break;
            }

            read += chunk;
        }

        return System.Text.Encoding.UTF8.GetString(buffer, 0, read);
    }

    /// <summary>
    /// Reads the documented health shape, tolerating everything else. A store on
    /// an older integration version, or one that answers with a bare status, is
    /// still observable; only an unparseable body counts as unhealthy.
    /// </summary>
    private static StoreProbeResult Parse(string body, int latencyMs)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;

            var status = root.TryGetProperty("status", out var statusElement) && statusElement.ValueKind is JsonValueKind.String
                ? statusElement.GetString()!
                : nameof(StoreHealthOutcome.Healthy);

            return new StoreProbeResult(
                Normalise(status),
                latencyMs,
                ReadString(root, "version"),
                ReadString(root, "environment"),
                ReadRaw(root, "dependencies"),
                ReadRaw(root, "features"),
                ReadString(root, "detail"));
        }
        catch (JsonException)
        {
            return new StoreProbeResult(
                nameof(StoreHealthOutcome.Unhealthy),
                latencyMs,
                null,
                null,
                null,
                null,
                "The store answered with a body that is not the documented health payload.");
        }
    }

    private static string Normalise(string status) => status.Trim().ToLowerInvariant() switch
    {
        "healthy" or "ok" or "up" or "pass" => nameof(StoreHealthOutcome.Healthy),
        "degraded" or "warn" or "warning" => nameof(StoreHealthOutcome.Degraded),
        _ => nameof(StoreHealthOutcome.Unhealthy),
    };

    private static string? ReadString(JsonElement root, string property) =>
        root.TryGetProperty(property, out var element) && element.ValueKind is JsonValueKind.String
            ? element.GetString()
            : null;

    private static string? ReadRaw(JsonElement root, string property) =>
        root.TryGetProperty(property, out var element) && element.ValueKind is JsonValueKind.Object or JsonValueKind.Array
            ? element.GetRawText()
            : null;

    /// <summary>
    /// One line, no inner-exception chain and no URL. Probe failures are stored
    /// and shown in the dashboard, and a connection string or a query parameter
    /// in an exception message would end up on a screen.
    /// </summary>
    private static string Summarise(Exception exception) => exception switch
    {
        TaskCanceledException => "The store did not answer within the timeout.",
        HttpRequestException { HttpRequestError: HttpRequestError.NameResolutionError } => "The store's domain did not resolve.",
        HttpRequestException { HttpRequestError: HttpRequestError.ConnectionError } => "The connection was refused.",
        HttpRequestException http => http.Message,
        _ => "The store could not be reached.",
    };
}

/// <summary>
/// The vocabulary the probe answers in. It mirrors <c>StoreHealthStatus</c> in
/// the stores module without depending on it: infrastructure describes what it
/// saw, and the module decides what that means for the link.
/// </summary>
internal enum StoreHealthOutcome
{
    Healthy,
    Degraded,
    Unhealthy,
    Unreachable,
}
