using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Knight.StoreAgent;

/// <summary>
/// Delivers a store's domain events to the Feature services that subscribed to
/// them.
///
/// A manifest declares which events a Feature wants (`webhooks`); this is the
/// half that delivers them. For each installed, enabled external-service Feature
/// subscribed to the event, it POSTs the payload to the Feature's webhook path,
/// signed with that Feature's own store secret — the canonical string the service
/// verifies (<see cref="KnightServiceProxyMiddleware.Sign"/>).
///
/// One attempt per subscriber; the caller decides what to do on failure. In this
/// store that caller is a durable outbox that persists the event first and retries
/// this call until it reports success, so nothing is lost across a restart. The
/// return value is what lets the outbox know: <c>true</c> only when every
/// subscriber accepted it (and when there are none to deliver to), <c>false</c>
/// when any subscriber failed or its secret has not arrived yet.
/// </summary>
public interface IKnightEventForwarder
{
    /// <returns><c>true</c> when every subscriber accepted the event.</returns>
    Task<bool> ForwardAsync(string eventName, object payload, CancellationToken cancellationToken = default);
}

internal sealed class KnightEventForwarder(
    FeatureRegistryAccessor registry,
    IHttpClientFactory clients,
    KnightAgentStatus status,
    IOptions<KnightOptions> options,
    ILogger<KnightEventForwarder> logger) : IKnightEventForwarder
{
    public async Task<bool> ForwardAsync(string eventName, object payload, CancellationToken cancellationToken = default)
    {
        var storeId = status.StoreId;
        if (string.IsNullOrEmpty(storeId))
        {
            // Not connected yet: there is no identity to sign as. Report failure
            // so the outbox holds the event and retries once the store connects.
            return false;
        }

        IReadOnlyDictionary<string, InstalledFeature> features;
        try
        {
            features = await registry.AllAsync(cancellationToken);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Could not read the feature registry to forward {Event}.", eventName);
            return false;
        }

        var body = JsonSerializer.SerializeToUtf8Bytes(payload);
        var allDelivered = true;

        foreach (var feature in features.Values)
        {
            var service = feature.Contract?.Service;
            if (!feature.Enabled || service is null)
            {
                continue;
            }

            foreach (var subscription in feature.Contract!.Webhooks)
            {
                if (!string.Equals(subscription.Event, eventName, StringComparison.Ordinal))
                {
                    continue;
                }

                var secret = FeatureConfigurationFile.SecretFor(options.Value.FeatureRoot, feature.Slug, service.SecretName);
                if (string.IsNullOrEmpty(secret))
                {
                    logger.LogWarning(
                        "No service secret for {Feature} yet; will retry {Event} once KNIGHT issues one.",
                        feature.Slug,
                        eventName);
                    allDelivered = false;
                    continue;
                }

                allDelivered &= await DeliverAsync(feature.Slug, service, subscription.Path, storeId, secret, body, cancellationToken);
            }
        }

        return allDelivered;
    }

    private async Task<bool> DeliverAsync(
        string slug,
        ServiceEndpoint service,
        string path,
        string storeId,
        string secret,
        byte[] body,
        CancellationToken cancellationToken)
    {
        var url = $"{service.BaseUrl.TrimEnd('/')}/{path.TrimStart('/')}";

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new ByteArrayContent(body),
            };
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            request.Headers.TryAddWithoutValidation("X-Knight-Store", storeId);
            request.Headers.TryAddWithoutValidation("X-Knight-Feature", slug);

            foreach (var (name, value) in KnightServiceProxyMiddleware.Sign(secret, "POST", path, body))
            {
                request.Headers.TryAddWithoutValidation(name, value);
            }

            using var response = await clients
                .CreateClient(KnightServiceProxyMiddleware.HttpClientName)
                .SendAsync(request, cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                return true;
            }

            logger.LogWarning("Forwarding {Path} to {Feature} returned {Status}.", path, slug, (int)response.StatusCode);
            return false;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "Forwarding {Path} to {Feature} failed.", path, slug);
            return false;
        }
    }
}
