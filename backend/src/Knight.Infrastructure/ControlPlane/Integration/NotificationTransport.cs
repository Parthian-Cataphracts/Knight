using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Knight.Application.Abstractions.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Observability;
using Observability.Domain;

namespace Knight.Infrastructure.ControlPlane.Integration;

/// <summary>
/// Actually delivers a notification.
///
/// The webhook path deliberately reuses the same hardened client the store
/// poller uses. A webhook URL is untrusted input in exactly the way a store URL
/// is — a customer types it in, and "https://hooks.example.com" resolving to
/// 169.254.169.254 is the ordinary shape of an SSRF attempt. Giving
/// notifications their own convenient <c>HttpClient</c> would have quietly
/// created a second, unguarded egress path out of the control plane
/// (docs/security-threat-model.md, SSRF).
///
/// Email has no SMTP implementation yet and says so plainly rather than
/// pretending: it records the message and reports success only for the in-app
/// and webhook kinds. A transport that silently swallowed mail would be worse
/// than one that is honestly not built — the failure would surface as "nobody
/// was told" during the first incident that needed it.
/// </summary>
internal sealed class NotificationTransport : INotificationTransport
{
    private readonly IHttpClientFactory _clients;
    private readonly ISecretProtector _secrets;
    private readonly ILogger<NotificationTransport> _logger;
    private readonly ObservabilityOptions _options;

    public NotificationTransport(
        IHttpClientFactory clients,
        ISecretProtector secrets,
        ILogger<NotificationTransport> logger,
        IOptions<ObservabilityOptions> options)
    {
        _clients = clients;
        _secrets = secrets;
        _logger = logger;
        _options = options.Value;
    }

    public Task<NotificationSendResult> SendAsync(
        NotificationChannel channel,
        NotificationDelivery delivery,
        CancellationToken cancellationToken) =>
        channel.Kind switch
        {
            // Nothing leaves KNIGHT: the delivery row *is* the notification, and
            // the dashboard reads it. It cannot fail to reach a network because
            // it never touches one.
            NotificationChannelKind.InApp => Task.FromResult(NotificationSendResult.Success),
            NotificationChannelKind.Webhook => SendWebhookAsync(channel, delivery, cancellationToken),
            NotificationChannelKind.Email => Task.FromResult(SendEmail(channel, delivery)),
            _ => Task.FromResult(NotificationSendResult.Fatal($"Unsupported channel kind '{channel.Kind}'.")),
        };

    private async Task<NotificationSendResult> SendWebhookAsync(
        NotificationChannel channel,
        NotificationDelivery delivery,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(channel.Endpoint))
        {
            return NotificationSendResult.Fatal("The channel has no destination.");
        }

        var payload = JsonSerializer.Serialize(new
        {
            id = delivery.Id,
            severity = delivery.Severity.ToString(),
            ruleKey = delivery.RuleKey,
            subject = delivery.Subject.ToString(),
            subjectId = delivery.SubjectId,
            title = delivery.Title,
            body = delivery.Body,
            occurredAt = delivery.CreatedAt,
        });

        using var request = new HttpRequestMessage(HttpMethod.Post, channel.Endpoint)
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json"),
        };

        // A receiver that cannot tell a real notification from a forged one is a
        // receiver that must not act on either. The signature covers the exact
        // bytes sent, so a proxy re-encoding the body invalidates it — which is
        // the correct outcome.
        if (channel.SecretCipher is { } cipher)
        {
            request.Headers.TryAddWithoutValidation("X-Knight-Signature", Sign(cipher, payload));
            request.Headers.TryAddWithoutValidation("X-Knight-Delivery", delivery.Id.ToString());
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_options.WebhookTimeout);

        try
        {
            var client = _clients.CreateClient(StoreOutboundHttp.ClientName);

            using var response = await client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                timeout.Token);

            if (response.IsSuccessStatusCode)
            {
                return NotificationSendResult.Success;
            }

            // 4xx means the request is wrong and will stay wrong; retrying it six
            // more times over the next hour helps nobody and looks like an attack
            // to whoever is receiving it. 429 is the exception: it is a 4xx that
            // explicitly means "try later".
            var permanent = response.StatusCode is not HttpStatusCode.TooManyRequests &&
                            (int)response.StatusCode is >= 400 and < 500;

            var error = $"The endpoint answered {(int)response.StatusCode} {response.ReasonPhrase}.";

            return permanent
                ? NotificationSendResult.Fatal(error)
                : NotificationSendResult.Transient(error);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return NotificationSendResult.Transient($"The endpoint did not answer within {_options.WebhookTimeout}.");
        }
        catch (HttpRequestException exception)
        {
            // Includes the egress policy refusing the resolved address. Transient
            // rather than fatal: DNS changes, and a destination that is refused
            // today may be legitimate tomorrow.
            _logger.LogWarning(
                exception,
                "Webhook delivery {DeliveryId} to channel {ChannelId} failed.",
                delivery.Id,
                channel.Id);

            return NotificationSendResult.Transient(exception.Message);
        }
    }

    private NotificationSendResult SendEmail(NotificationChannel channel, NotificationDelivery delivery)
    {
        // Deliberately a refusal, not a silent success. KNIGHT has no mail
        // transport configured, and reporting "delivered" for a message that went
        // nowhere would make the delivery log — the record of who was told what —
        // a lie exactly where it matters most.
        _logger.LogWarning(
            "Email channel {ChannelId} cannot deliver {DeliveryId}: no mail transport is configured on this host.",
            channel.Id,
            delivery.Id);

        return NotificationSendResult.Fatal(
            "No mail transport is configured. Use a webhook or the in-app channel until one is.");
    }

    private string Sign(string cipher, string payload)
    {
        var secret = _secrets.Unprotect(cipher);
        var signature = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(payload));

        return $"sha256={Convert.ToHexStringLower(signature)}";
    }
}
