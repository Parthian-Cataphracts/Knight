using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Knight.StoreAgent;

/// <summary>
/// Calls one installed Feature service synchronously and returns its JSON answer.
///
/// The event forwarder pushes fire-and-forget notifications; this is the other
/// shape — a request the store needs an answer to before it can finish its own
/// work, such as asking the promotions Feature what a basket is worth at
/// checkout. It resolves the Feature by slug, signs the request with that
/// Feature's own store secret (the canonical string the service verifies, see
/// <see cref="KnightServiceProxyMiddleware.Sign"/>), and parses the response.
///
/// It never throws for an operational failure — a Feature that is not installed,
/// not connected yet, missing its secret, down, or slow returns <c>null</c>, so
/// the caller can treat "no answer" as "no effect" and never let a Feature block
/// the store's own transaction.
/// </summary>
public interface IKnightFeatureClient
{
    /// <returns>The parsed JSON response, or <c>null</c> if the call could not be made or did not succeed.</returns>
    Task<JsonDocument?> CallAsync(
        string slug,
        string method,
        string path,
        object? payload,
        CancellationToken cancellationToken = default);
}

internal sealed class KnightFeatureClient(
    FeatureRegistryAccessor registry,
    IHttpClientFactory clients,
    KnightAgentStatus status,
    IOptions<KnightOptions> options,
    ILogger<KnightFeatureClient> logger) : IKnightFeatureClient
{
    public async Task<JsonDocument?> CallAsync(
        string slug,
        string method,
        string path,
        object? payload,
        CancellationToken cancellationToken = default)
    {
        var storeId = status.StoreId;
        if (string.IsNullOrEmpty(storeId))
        {
            return null;
        }

        InstalledFeature? feature;
        try
        {
            var features = await registry.AllAsync(cancellationToken);
            features.TryGetValue(slug, out feature);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Could not read the feature registry to call {Feature}.", slug);
            return null;
        }

        var service = feature?.Contract?.Service;
        if (feature is null || !feature.Enabled || service is null)
        {
            return null;
        }

        var secret = FeatureConfigurationFile.SecretFor(options.Value.FeatureRoot, feature.Slug, service.SecretName);
        if (string.IsNullOrEmpty(secret))
        {
            return null;
        }

        var body = payload is null ? [] : JsonSerializer.SerializeToUtf8Bytes(payload);
        var url = $"{service.BaseUrl.TrimEnd('/')}/{path.TrimStart('/')}";
        var verb = new HttpMethod(method.ToUpperInvariant());

        try
        {
            using var request = new HttpRequestMessage(verb, url);
            if (body.Length > 0)
            {
                request.Content = new ByteArrayContent(body);
                request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            }
            request.Headers.TryAddWithoutValidation("X-Knight-Store", storeId);
            request.Headers.TryAddWithoutValidation("X-Knight-Feature", slug);
            foreach (var (name, value) in KnightServiceProxyMiddleware.Sign(secret, verb.Method, path, body))
            {
                request.Headers.TryAddWithoutValidation(name, value);
            }

            using var response = await clients
                .CreateClient(KnightServiceProxyMiddleware.HttpClientName)
                .SendAsync(request, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("Calling {Path} on {Feature} returned {Status}.", path, slug, (int)response.StatusCode);
                return null;
            }

            var stream = await response.Content.ReadAsByteArrayAsync(cancellationToken);
            return stream.Length == 0 ? null : JsonDocument.Parse(stream);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "Calling {Path} on {Feature} failed.", path, slug);
            return null;
        }
    }
}
