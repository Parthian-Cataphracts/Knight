using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Knight.StoreAgent;

/// <summary>KNIGHT could not be reached, or answered with something unusable.</summary>
public sealed class KnightUnavailableException(string message, Exception? inner = null)
    : Exception(message, inner);

/// <summary>KNIGHT answered, and refused. Retrying the same call will not help.</summary>
public sealed class KnightRejectedException(HttpStatusCode status, string detail, string code = "")
    : Exception($"KNIGHT refused the request ({(int)status}): {detail}")
{
    public HttpStatusCode Status { get; } = status;

    public string Detail { get; } = detail;

    public string Code { get; } = code;
}

/// <summary>
/// The HTTP client to KNIGHT: handshake, heartbeat, claim, report.
///
/// Three properties matter more than anything else here, and they are the same
/// three the Django and node reference stores are built around, because they are
/// properties of the contract rather than of any language:
///
/// <list type="bullet">
/// <item><b>Outbound only.</b> The store asks for work; KNIGHT never connects
/// inward. That is what lets a store sit behind a firewall with no inbound port
/// and still receive Features.</item>
/// <item><b>A 401 is recoverable exactly once.</b> Tokens expire; when one does,
/// the client handshakes again and retries. A second 401 means the credential is
/// wrong and retrying harder will not fix it.</item>
/// <item><b>Nothing waits on a shopper's behalf.</b> Every call has a timeout and
/// every caller is a background service.</item>
/// </list>
/// </summary>
public sealed class KnightClient
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly HttpClient _http;
    private readonly KnightOptions _options;
    private readonly KnightConnection _connection;
    private readonly KnightAgentStatus _status;
    private readonly ILogger<KnightClient> _logger;
    private readonly SemaphoreSlim _handshakeLock = new(1, 1);

    private string? _token;

    /// <summary>
    /// Which client id the current token was minted from.
    ///
    /// Kept so a credential entered in a panel takes effect without a restart:
    /// the token from the previous one is dropped rather than sent to a control
    /// plane that has never seen it, which would look to an operator like the
    /// new credential being wrong.
    /// </summary>
    private string _tokenClientId = string.Empty;

    public KnightClient(
        HttpClient http,
        IOptions<KnightOptions> options,
        KnightConnection connection,
        KnightAgentStatus status,
        ILogger<KnightClient> logger)
    {
        _options = options.Value;
        _connection = connection;
        _status = status;
        _logger = logger;
        _http = http;
        _http.Timeout = _options.Timeout;
    }

    /// <summary>The store as KNIGHT knows it, from the last handshake. Null until one has happened.</summary>
    public StoreIdentity? Store { get; private set; }

    /// <summary>
    /// Exchanges the client credential for a store token.
    ///
    /// The nonce makes a captured request body useless a second time: KNIGHT
    /// remembers it for the length of its window and refuses a replay.
    /// </summary>
    public async Task<StoreIdentity> HandshakeAsync(CancellationToken cancellationToken)
    {
        var credential = await _connection.CurrentAsync(cancellationToken);

        var body = new
        {
            clientId = credential.ClientId,
            clientSecret = credential.ClientSecret,
            environment = credential.Environment,
            storeVersion = _options.StoreVersion,
            runtime = $".NET {System.Environment.Version}",
            nonce = Guid.NewGuid().ToString("n"),
        };

        var identity = await SendAsync<StoreIdentity>(
            HttpMethod.Post,
            "api/v1/ingest/handshake",
            body,
            authenticated: false,
            cancellationToken)
            ?? throw new KnightUnavailableException("The handshake succeeded and returned nothing.");

        _token = identity.AccessToken;
        _tokenClientId = credential.ClientId;
        Store = identity;
        _status.RecordHandshake(identity);

        _logger.LogInformation(
            "Connected to KNIGHT as {StoreName} ({Slug}), integration {Status}.",
            identity.StoreName,
            identity.Slug,
            identity.IntegrationStatus);

        return identity;
    }

    /// <summary>
    /// What this store runs, for KNIGHT's compatibility checks.
    ///
    /// <c>name</c> before any version of anything: KNIGHT decides from it which
    /// of the other names mean anything, and refuses a Feature built for another
    /// runtime by name rather than by failing version comparisons it cannot
    /// make. A store that omits the name cannot be planned against at all.
    /// </summary>
    public static Dictionary<string, string> Runtime() => new()
    {
        ["name"] = "dotnet",
        ["dotnet"] = System.Environment.Version.ToString(),
    };

    public async Task HeartbeatAsync(
        string status,
        IReadOnlyCollection<string> features,
        IReadOnlyDictionary<string, object>? dependencies,
        string? detail,
        CancellationToken cancellationToken)
    {
        var credential = await _connection.CurrentAsync(cancellationToken);

        var body = new
        {
            environment = credential.Environment,
            status,
            storeVersion = _options.StoreVersion,
            dependencies = dependencies ?? new Dictionary<string, object>(),
            runtime = Runtime(),
            features,
            detail,
        };

        await SendAsync<object>(HttpMethod.Post, "api/v1/ingest/heartbeat", body, true, cancellationToken);

        _status.RecordHeartbeat();
    }

    /// <summary>
    /// Claims this store's next installation job, or null.
    ///
    /// Null is the overwhelmingly common answer and is not an error: KNIGHT
    /// answers 204 when there is nothing queued.
    /// </summary>
    public Task<AgentJob?> ClaimJobAsync(CancellationToken cancellationToken) =>
        SendAsync<AgentJob>(HttpMethod.Post, "api/v1/ingest/jobs/next", null, true, cancellationToken);

    /// <summary>
    /// Reports one step's outcome.
    ///
    /// Safe to call twice for the same step: KNIGHT updates it in place rather
    /// than appending, because an agent that finished a step and lost the reply
    /// will report it again, and treating the repeat as a second execution would
    /// be a job that ran a migration twice.
    /// </summary>
    public Task ReportStepAsync(
        Guid jobId,
        string step,
        string status,
        string? output,
        string? errorCode,
        long durationMilliseconds,
        CancellationToken cancellationToken)
    {
        var body = new { step, status, output, errorCode, durationMilliseconds };

        return SendAsync<object>(HttpMethod.Post, $"api/v1/ingest/jobs/{jobId}/steps", body, true, cancellationToken);
    }

    public Task CompleteJobAsync(
        Guid jobId,
        bool succeeded,
        string? failureCode,
        string? failureMessage,
        string? installedVersion,
        string? health,
        CancellationToken cancellationToken)
    {
        var body = new
        {
            succeeded,
            failureCode,
            failureMessage,
            rollbackOutcome = (string?)null,
            installedVersion,
            health,
        };

        return SendAsync<object>(HttpMethod.Post, $"api/v1/ingest/jobs/{jobId}/complete", body, true, cancellationToken);
    }

    /// <summary>Downloads an artifact, refusing anything past the configured ceiling.</summary>
    public async Task<byte[]> DownloadAsync(Uri url, long declaredBytes, CancellationToken cancellationToken)
    {
        if (declaredBytes > _options.MaxArtifactBytes)
        {
            throw new StepFailedException(
                "fetch.oversized",
                $"The job declares {declaredBytes} bytes and this store accepts at most {_options.MaxArtifactBytes}.");
        }

        // A separate, unauthenticated request: the download URL is signed by the
        // artifact store and carries its own authority. Sending KNIGHT's token
        // to whatever host the URL names would hand a store credential to a CDN.
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw new StepFailedException(
                "fetch.failed",
                $"The artifact could not be downloaded: {(int)response.StatusCode} {response.ReasonPhrase}.");
        }

        var length = response.Content.Headers.ContentLength;

        if (length is > 0 && length > _options.MaxArtifactBytes)
        {
            throw new StepFailedException(
                "fetch.oversized",
                $"The artifact is {length} bytes and this store accepts at most {_options.MaxArtifactBytes}.");
        }

        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);

        // Checked again after reading, because Content-Length is the server's
        // claim about the body rather than the body.
        if (bytes.LongLength > _options.MaxArtifactBytes)
        {
            throw new StepFailedException(
                "fetch.oversized",
                $"The artifact is {bytes.LongLength} bytes and this store accepts at most {_options.MaxArtifactBytes}.");
        }

        if (declaredBytes > 0 && bytes.LongLength != declaredBytes)
        {
            throw new StepFailedException(
                "fetch.wrong_size",
                $"The job says {declaredBytes} bytes and {bytes.LongLength} arrived.");
        }

        return bytes;
    }

    private async Task<T?> SendAsync<T>(
        HttpMethod method,
        string path,
        object? body,
        bool authenticated,
        CancellationToken cancellationToken,
        bool retryOn401 = true)
        where T : class
    {
        if (authenticated && _token is null)
        {
            await EnsureTokenAsync(cancellationToken);
        }

        // Built per request rather than from a base address fixed at start-up:
        // which control plane a store talks to is part of the credential, and a
        // credential can be entered in a panel while this process is running.
        var credential = await _connection.CurrentAsync(cancellationToken);
        var target = new Uri(new Uri(credential.BaseUrl.TrimEnd('/') + "/"), path);

        using var request = new HttpRequestMessage(method, target);

        if (body is not null)
        {
            request.Content = JsonContent.Create(body, options: Json);
        }

        if (authenticated)
        {
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _token);
        }

        HttpResponseMessage response;

        try
        {
            response = await _http.SendAsync(request, cancellationToken);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            throw new KnightUnavailableException($"{method} {path} could not be reached.", exception);
        }

        using (response)
        {
            if (response.StatusCode is HttpStatusCode.Unauthorized && authenticated && retryOn401)
            {
                // Expired, or minted before a credential rotation.
                _token = null;
                await EnsureTokenAsync(cancellationToken);

                return await SendAsync<T>(method, path, body, authenticated, cancellationToken, retryOn401: false);
            }

            if (response.StatusCode is HttpStatusCode.NoContent)
            {
                return null;
            }

            var text = await response.Content.ReadAsStringAsync(cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                throw new KnightRejectedException(response.StatusCode, Describe(text, response), CodeOf(text));
            }

            return string.IsNullOrWhiteSpace(text) ? null : JsonSerializer.Deserialize<T>(text, Json);
        }
    }

    private async Task EnsureTokenAsync(CancellationToken cancellationToken)
    {
        await _handshakeLock.WaitAsync(cancellationToken);

        try
        {
            // Checked inside the lock: the heartbeat and the job poller can both
            // arrive here at once, and two handshakes would burn two nonces and
            // leave one of the two tokens orphaned.
            var credential = await _connection.CurrentAsync(cancellationToken);

            // A different credential than the one this token came from is a
            // store that has just been reconnected, possibly to a different
            // control plane. Reusing the token would send it somewhere that has
            // never issued one.
            if (_token is null || !string.Equals(_tokenClientId, credential.ClientId, StringComparison.Ordinal))
            {
                _token = null;
                await HandshakeAsync(cancellationToken);
            }
        }
        finally
        {
            _handshakeLock.Release();
        }
    }

    private static string Describe(string text, HttpResponseMessage response)
    {
        try
        {
            using var document = JsonDocument.Parse(text);
            var root = document.RootElement;

            if (root.TryGetProperty("detail", out var detail) && detail.ValueKind is JsonValueKind.String)
            {
                return detail.GetString()!;
            }

            if (root.TryGetProperty("title", out var title) && title.ValueKind is JsonValueKind.String)
            {
                return title.GetString()!;
            }
        }
        catch (JsonException)
        {
            // A body that is not a problem document is still a body worth showing.
        }

        return string.IsNullOrWhiteSpace(text) ? response.ReasonPhrase ?? "no detail" : text[..Math.Min(text.Length, 300)];
    }

    private static string CodeOf(string text)
    {
        try
        {
            using var document = JsonDocument.Parse(text);

            return document.RootElement.TryGetProperty("errorCode", out var code) && code.ValueKind is JsonValueKind.String
                ? code.GetString()!
                : string.Empty;
        }
        catch (JsonException)
        {
            return string.Empty;
        }
    }
}
