using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Knight.Application.Abstractions.ControlPlane;
using Knight.Application.Exceptions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Knight.Infrastructure.ControlPlane.Integration;

/// <summary>
/// The control secret KNIGHT signs a Feature's service with, per Feature.
///
/// Per Feature and not one for the fleet: these services are operated
/// separately, and a single control secret would mean whoever runs one of them
/// holds the key that can issue a store credential on all of them.
///
/// Bound from configuration (section "ServiceControlPlane"), which means it
/// arrives the way every other deployment secret does — an environment variable
/// or a secret store — and never from a manifest, which is public.
/// </summary>
public sealed class ServiceControlPlaneOptions
{
    public const string SectionName = "ServiceControlPlane";

    /// <summary>Feature slug to control secret. Empty means this deployment issues no service credentials.</summary>
    public Dictionary<string, string> Secrets { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>How long KNIGHT waits for a service to answer a control call.</summary>
    public int TimeoutSeconds { get; init; } = 10;
}

/// <summary>
/// KNIGHT talking to a Feature's service about the stores it serves.
///
/// The same signature the store uses, over the same canonical string — method,
/// path, timestamp, nonce, body digest — under a different secret. Reusing the
/// scheme is deliberate: a service that had to implement a second way of
/// checking who is calling would have a second way of getting it wrong, and this
/// is the caller that can issue credentials.
///
/// It goes out through the hardened store client, so the SSRF policy that guards
/// every other outbound call guards this one too. A base URL comes out of a
/// manifest, and a manifest is written by somebody else.
/// </summary>
internal sealed class ServiceControlPlaneClient : IServiceControlPlane
{
    private readonly IHttpClientFactory _clients;
    private readonly ILogger<ServiceControlPlaneClient> _logger;
    private readonly ServiceControlPlaneOptions _options;

    public ServiceControlPlaneClient(
        IHttpClientFactory clients,
        ILogger<ServiceControlPlaneClient> logger,
        IOptions<ServiceControlPlaneOptions> options)
    {
        _clients = clients;
        _logger = logger;
        _options = options.Value;
    }

    public Task RegisterAsync(
        ServiceEndpointDescriptor endpoint,
        string secret,
        CancellationToken cancellationToken) =>
        SendAsync(
            endpoint,
            "/knight/stores/register",
            new
            {
                storeId = endpoint.StoreId,
                slug = endpoint.StoreSlug,
                secret,
                enabled = true,
            },
            cancellationToken);

    public Task RotateAsync(
        ServiceEndpointDescriptor endpoint,
        string secret,
        int overlapSeconds,
        CancellationToken cancellationToken) =>
        SendAsync(
            endpoint,
            "/knight/stores/rotate",
            new
            {
                storeId = endpoint.StoreId,
                secret,
                overlapSeconds,
            },
            cancellationToken);

    public Task RevokeAsync(ServiceEndpointDescriptor endpoint, CancellationToken cancellationToken) =>
        SendAsync(
            endpoint,
            "/knight/stores/revoke",
            new { storeId = endpoint.StoreId },
            cancellationToken);

    private async Task SendAsync(
        ServiceEndpointDescriptor endpoint,
        string path,
        object payload,
        CancellationToken cancellationToken)
    {
        if (!_options.Secrets.TryGetValue(endpoint.FeatureSlug, out var controlSecret)
            || string.IsNullOrWhiteSpace(controlSecret))
        {
            // Refused, never sent unsigned. A service that accepted an unsigned
            // control call would accept anybody's, and this is the call that
            // issues credentials.
            throw new ConflictException(
                $"This deployment holds no control secret for '{endpoint.FeatureSlug}', " +
                "so it cannot issue that Feature's stores a credential.");
        }

        var body = JsonSerializer.SerializeToUtf8Bytes(payload);
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
        var nonce = Guid.NewGuid().ToString("N");

        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(endpoint.BaseUrl, path))
        {
            Content = new ByteArrayContent(body),
        };

        request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        request.Headers.TryAddWithoutValidation("X-Knight-Timestamp", timestamp);
        request.Headers.TryAddWithoutValidation("X-Knight-Nonce", nonce);
        request.Headers.TryAddWithoutValidation(
            "X-Knight-Signature",
            "sha256=" + Sign(controlSecret, "POST", path, timestamp, nonce, body));

        var client = _clients.CreateClient(StoreOutboundHttp.ClientName);
        client.Timeout = TimeSpan.FromSeconds(Math.Clamp(_options.TimeoutSeconds, 1, 60));

        HttpResponseMessage response;

        try
        {
            response = await client.SendAsync(request, cancellationToken);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            // A service that cannot be reached must not leave a store holding a
            // secret it has not heard of, so this is a failure of the whole
            // operation rather than something to log and continue past.
            _logger.LogWarning(
                exception,
                "The {Feature} service did not answer a control call to {Path}.",
                endpoint.FeatureSlug,
                path);

            throw new ConflictException(
                $"The '{endpoint.FeatureSlug}' service did not answer, so no credential was changed.");
        }

        using (response)
        {
            if (response.IsSuccessStatusCode)
            {
                return;
            }

            var detail = await response.Content.ReadAsStringAsync(cancellationToken);

            _logger.LogWarning(
                "The {Feature} service refused a control call to {Path} with {Status}.",
                endpoint.FeatureSlug,
                path,
                (int)response.StatusCode);

            throw new ConflictException(
                $"The '{endpoint.FeatureSlug}' service refused the call with {(int)response.StatusCode}: " +
                Trim(detail));
        }
    }

    /// <summary>
    /// The canonical string, built here and never sent.
    ///
    /// Both ends derive it independently; a signature over a string one party
    /// supplied proves only that the party agrees with itself.
    /// </summary>
    private static string Sign(string secret, string method, string path, string timestamp, string nonce, byte[] body)
    {
        var digest = Convert.ToHexStringLower(SHA256.HashData(body));
        var message = string.Join('\n', method.ToUpperInvariant(), path, timestamp, nonce, digest);

        return Convert.ToHexStringLower(
            HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(message)));
    }

    /// <summary>Somebody else's error text, bounded before it goes anywhere.</summary>
    private static string Trim(string detail) =>
        detail.Length <= 300 ? detail : detail[..300];
}
