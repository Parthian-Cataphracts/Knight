using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Knight.StoreAgent;

/// <summary>
/// Forwards a store's domain events to the Feature services that subscribed to
/// them.
///
/// A manifest declares which events a Feature wants (`webhooks`); this is the
/// half that actually delivers them. For each installed, enabled external-service
/// Feature subscribed to the event, it POSTs the payload to the Feature's webhook
/// path, signed with that Feature's own store secret — the exact canonical string
/// the service verifies (<see cref="KnightServiceProxyMiddleware.Sign"/>). Delivery is
/// at-least-once with a short retry, and every service is idempotent on the
/// event id, so a duplicate is dropped rather than double-counted.
///
/// The store calls this <b>after</b> its own transaction commits: a Feature that
/// is slow or down must never hold up, or roll back, an order the shop already
/// took. Losing a delivery on a crash is the gap a durable outbox would close;
/// until then the retry covers the ordinary failures.
/// </summary>
public interface IKnightEventForwarder
{
    Task ForwardAsync(string eventName, object payload, CancellationToken cancellationToken = default);
}

internal sealed class KnightEventForwarder(
    FeatureRegistryAccessor registry,
    IHttpClientFactory clients,
    KnightAgentStatus status,
    IOptions<KnightOptions> options,
    ILogger<KnightEventForwarder> logger) : IKnightEventForwarder
{
    private const int MaxAttempts = 3;

    public async Task ForwardAsync(string eventName, object payload, CancellationToken cancellationToken = default)
    {
        var storeId = status.StoreId;
        if (string.IsNullOrEmpty(storeId))
        {
            // Not connected yet: there is no identity to sign as, and nothing has
            // been delivered to subscribe. Silently skip rather than fail the shop.
            return;
        }

        IReadOnlyDictionary<string, InstalledFeature> features;
        try
        {
            features = await registry.AllAsync(cancellationToken);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Could not read the feature registry to forward {Event}.", eventName);
            return;
        }

        byte[]? body = null;

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
                        "No service secret for {Feature} yet; cannot forward {Event} until KNIGHT issues one.",
                        feature.Slug,
                        eventName);
                    continue;
                }

                body ??= JsonSerializer.SerializeToUtf8Bytes(payload);
                await DeliverAsync(feature.Slug, service, subscription.Path, storeId, secret, body, cancellationToken);
            }
        }
    }

    private async Task DeliverAsync(
        string slug,
        ServiceEndpoint service,
        string path,
        string storeId,
        string secret,
        byte[] body,
        CancellationToken cancellationToken)
    {
        var url = $"{service.BaseUrl.TrimEnd('/')}/{path.TrimStart('/')}";

        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, url)
                {
                    Content = new ByteArrayContent(body),
                };
                request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
                request.Headers.TryAddWithoutValidation("X-Knight-Store", storeId);
                request.Headers.TryAddWithoutValidation("X-Knight-Feature", slug);

                // The same signature scheme the proxy uses, over the webhook path,
                // under this Feature's own secret — which is what the service checks.
                foreach (var (name, value) in KnightServiceProxyMiddleware.Sign(secret, "POST", path, body))
                {
                    request.Headers.TryAddWithoutValidation(name, value);
                }

                using var response = await clients
                    .CreateClient(KnightServiceProxyMiddleware.HttpClientName)
                    .SendAsync(request, cancellationToken);

                if (response.IsSuccessStatusCode)
                {
                    return;
                }

                logger.LogWarning(
                    "Forwarding {Path} to {Feature} returned {Status} (attempt {Attempt}/{Max}).",
                    path, slug, (int)response.StatusCode, attempt, MaxAttempts);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogWarning(exception, "Forwarding {Path} to {Feature} failed (attempt {Attempt}/{Max}).", path, slug, attempt, MaxAttempts);
            }

            if (attempt < MaxAttempts)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(200 * attempt), cancellationToken);
            }
        }

        logger.LogError("Gave up forwarding {Path} to {Feature} after {Max} attempts.", path, slug, MaxAttempts);
    }
}
