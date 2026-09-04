using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Knight.Application.Abstractions.Time;
using Microsoft.Extensions.Options;
using PlatformBilling.Domain;

namespace PlatformBilling.Payments;

/// <summary>
/// Stripe, behind the <see cref="IPlatformPaymentProvider"/> abstraction — the
/// first real gateway next to the simulated one (docs/self-service-saas-plan.md §11).
///
/// The security-critical half is the webhook: it is the only path that activates a
/// paid subscription, so verification here is Stripe's own scheme, exactly — the
/// signed payload is <c>{timestamp}.{raw body}</c>, the HMAC-SHA256 of it under the
/// endpoint's signing secret is compared in constant time against each <c>v1</c>
/// signature in the <c>Stripe-Signature</c> header, and a timestamp outside the
/// tolerance is refused so a captured callback cannot be replayed later. None of
/// that depends on reaching Stripe, which is why it is unit-tested directly.
///
/// Opening a checkout does reach Stripe: it creates a hosted Checkout Session and
/// hands back the URL to send the browser to and the id the webhook will name it
/// by. <c>client_reference_id</c> carries KNIGHT's own checkout-session id so the
/// two systems can always be reconciled.
/// </summary>
internal sealed class StripePaymentProvider : IPlatformPaymentProvider
{
    private const string CheckoutSessionsEndpoint = "https://api.stripe.com/v1/checkout/sessions";

    private readonly HttpClient _http;
    private readonly StripeOptions _options;
    private readonly IDateTimeProvider _clock;

    public StripePaymentProvider(HttpClient http, IOptions<PlatformBillingOptions> options, IDateTimeProvider clock)
    {
        _http = http;
        _options = options.Value.Stripe;
        _clock = clock;
    }

    public string Name => "stripe";

    public async Task<CheckoutStart> StartCheckoutAsync(CheckoutSession session, CancellationToken cancellationToken)
    {
        if (!_options.IsConfigured)
        {
            throw new InvalidOperationException("Stripe is not configured; no secret key is set.");
        }

        // Stripe amounts are in the currency's smallest unit — cents for EUR/USD.
        var minorUnits = (long)Math.Round(session.Amount * 100m, MidpointRounding.AwayFromZero);

        var form = new Dictionary<string, string>
        {
            ["mode"] = "payment",
            ["success_url"] = _options.SuccessUrl,
            ["cancel_url"] = _options.CancelUrl,
            // The bridge back to KNIGHT's own record, carried on the session so a
            // webhook — or a human reconciling an account — can always find it.
            ["client_reference_id"] = session.Id.ToString("n"),
            ["line_items[0][quantity]"] = "1",
            ["line_items[0][price_data][currency]"] = session.Currency.ToLowerInvariant(),
            ["line_items[0][price_data][unit_amount]"] = minorUnits.ToString(CultureInfo.InvariantCulture),
            ["line_items[0][price_data][product_data][name]"] = "KNIGHT store subscription",
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, CheckoutSessionsEndpoint)
        {
            Content = new FormUrlEncodedContent(form),
        };
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _options.SecretKey);

        using var response = await _http.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Stripe refused to open a checkout ({(int)response.StatusCode}).");
        }

        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        var providerSessionId = root.TryGetProperty("id", out var id) ? id.GetString() : null;
        var url = root.TryGetProperty("url", out var u) ? u.GetString() : null;

        if (string.IsNullOrWhiteSpace(providerSessionId) || string.IsNullOrWhiteSpace(url))
        {
            throw new InvalidOperationException("Stripe returned a checkout session with no id or url.");
        }

        return new CheckoutStart(url, providerSessionId);
    }

    public bool VerifySignature(string payload, string? signature)
    {
        if (string.IsNullOrWhiteSpace(_options.WebhookSecret) || string.IsNullOrWhiteSpace(signature))
        {
            return false;
        }

        // Stripe-Signature: t=<unix>,v1=<hex>,v1=<hex>… — parse the timestamp and
        // every v1 rather than assuming one.
        long? timestamp = null;
        var signatures = new List<string>();
        foreach (var part in signature.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var separator = part.IndexOf('=');
            if (separator <= 0)
            {
                continue;
            }

            var name = part[..separator];
            var value = part[(separator + 1)..];

            if (name == "t" && long.TryParse(value, out var parsed))
            {
                timestamp = parsed;
            }
            else if (name == "v1")
            {
                signatures.Add(value);
            }
        }

        if (timestamp is not { } sentAt || signatures.Count == 0)
        {
            return false;
        }

        // A callback whose timestamp is too old (or from the future) is refused: it
        // is what turns a captured, valid signature into something that cannot be
        // replayed a day later.
        var age = Math.Abs(_clock.UtcNow.ToUnixTimeSeconds() - sentAt);
        if (age > _options.SignatureToleranceSeconds)
        {
            return false;
        }

        var signedPayload = $"{sentAt}.{payload}";
        var expected = Convert.ToHexStringLower(
            HMACSHA256.HashData(Encoding.UTF8.GetBytes(_options.WebhookSecret), Encoding.UTF8.GetBytes(signedPayload)));
        var expectedBytes = Encoding.ASCII.GetBytes(expected);

        // Constant-time against every offered signature, so neither the presence of
        // a match nor its position leaks.
        var matched = false;
        foreach (var candidate in signatures)
        {
            if (CryptographicOperations.FixedTimeEquals(expectedBytes, Encoding.ASCII.GetBytes(candidate.ToLowerInvariant())))
            {
                matched = true;
            }
        }

        return matched;
    }

    public bool TryParseEvent(string payload, out ProviderPaymentEvent paymentEvent)
    {
        paymentEvent = null!;

        try
        {
            using var document = JsonDocument.Parse(payload);
            var root = document.RootElement;

            if (!root.TryGetProperty("type", out var typeElement) ||
                !root.TryGetProperty("data", out var data) ||
                !data.TryGetProperty("object", out var obj))
            {
                return false;
            }

            var type = typeElement.GetString();
            var kind = type switch
            {
                "checkout.session.completed" => PlatformPaymentEventKind.PaymentSucceeded,
                "checkout.session.async_payment_succeeded" => PlatformPaymentEventKind.PaymentSucceeded,
                "checkout.session.async_payment_failed" => PlatformPaymentEventKind.PaymentFailed,
                "checkout.session.expired" => PlatformPaymentEventKind.PaymentFailed,
                _ => PlatformPaymentEventKind.Unhandled,
            };

            // The Stripe Checkout Session id — the same id StartCheckoutAsync
            // recorded as the provider session.
            var providerSessionId = obj.TryGetProperty("id", out var sessionId) ? sessionId.GetString() : null;
            var providerTransactionId = obj.TryGetProperty("payment_intent", out var pi) ? pi.GetString() : null;

            if (string.IsNullOrWhiteSpace(providerSessionId))
            {
                return false;
            }

            if (kind is PlatformPaymentEventKind.PaymentSucceeded && string.IsNullOrWhiteSpace(providerTransactionId))
            {
                // A completed session with no payment intent is not something to
                // settle a charge against.
                providerTransactionId = providerSessionId;
            }

            paymentEvent = new ProviderPaymentEvent(kind, providerSessionId.Trim(), (providerTransactionId ?? string.Empty).Trim());
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
