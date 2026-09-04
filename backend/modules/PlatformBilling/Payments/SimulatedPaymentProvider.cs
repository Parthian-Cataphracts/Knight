using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using PlatformBilling.Domain;

namespace PlatformBilling.Payments;

/// <summary>
/// The stand-in provider that runs the self-service journey with no real gateway
/// (docs/self-service-saas-plan.md §11). It behaves like a real one in every way
/// the rest of the system can observe: it issues a checkout URL and a session id,
/// it signs — and verifies — its webhooks with an HMAC when a secret is set, and
/// it delivers a small, explicit event body.
///
/// A webhook body is JSON:
/// <c>{ "type": "payment_succeeded", "providerSessionId": "...", "providerTransactionId": "..." }</c>.
/// The signature, when required, is the lowercase hex HMAC-SHA256 of the raw body
/// under <see cref="PlatformBillingOptions.WebhookSecret"/> — the same shape a
/// real provider uses, so swapping in the real adapter changes only this class.
/// </summary>
internal sealed class SimulatedPaymentProvider : IPlatformPaymentProvider
{
    private readonly PlatformBillingOptions _options;

    public SimulatedPaymentProvider(IOptions<PlatformBillingOptions> options)
    {
        _options = options.Value;
    }

    public string Name => "simulated";

    public Task<CheckoutStart> StartCheckoutAsync(CheckoutSession session, CancellationToken cancellationToken)
    {
        // Deterministic from the session so a retried checkout call reuses the
        // same provider session rather than opening a second charge.
        var providerSessionId = $"sim_sess_{session.Id:n}";
        var separator = _options.CheckoutBaseUrl.Contains('?', StringComparison.Ordinal) ? '&' : '?';
        var checkoutUrl = $"{_options.CheckoutBaseUrl}{separator}session={providerSessionId}";
        return Task.FromResult(new CheckoutStart(checkoutUrl, providerSessionId));
    }

    public bool VerifySignature(string payload, string? signature)
    {
        // No secret configured is a development stance, not a bypass a real
        // provider allows: it accepts unsigned callbacks precisely so a local
        // journey needs no key, and the option exists so a deployment can require
        // one.
        if (string.IsNullOrEmpty(_options.WebhookSecret))
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(signature))
        {
            return false;
        }

        var expected = Sign(payload, _options.WebhookSecret);
        var provided = signature.Trim();

        // Fixed-time compare: a webhook signature is a credential, and a
        // length-or-content-leaking compare is the same mistake as an early-return
        // password check.
        return CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(expected),
            Encoding.ASCII.GetBytes(provided.ToLowerInvariant()));
    }

    public bool TryParseEvent(string payload, out ProviderPaymentEvent paymentEvent)
    {
        paymentEvent = null!;

        if (string.IsNullOrWhiteSpace(payload))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(payload);
            var root = document.RootElement;

            if (root.ValueKind is not JsonValueKind.Object)
            {
                return false;
            }

            var type = Read(root, "type");
            var providerSessionId = Read(root, "providerSessionId");
            var providerTransactionId = Read(root, "providerTransactionId");

            if (string.IsNullOrWhiteSpace(providerSessionId))
            {
                return false;
            }

            var kind = type switch
            {
                "payment_succeeded" => PlatformPaymentEventKind.PaymentSucceeded,
                "payment_failed" => PlatformPaymentEventKind.PaymentFailed,
                _ => PlatformPaymentEventKind.Unhandled,
            };

            // A succeeded charge with no transaction id is malformed; a failed or
            // unhandled one need not carry one.
            if (kind is PlatformPaymentEventKind.PaymentSucceeded && string.IsNullOrWhiteSpace(providerTransactionId))
            {
                return false;
            }

            paymentEvent = new ProviderPaymentEvent(
                kind,
                providerSessionId.Trim(),
                (providerTransactionId ?? string.Empty).Trim());
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string? Read(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.String
            ? value.GetString()
            : null;

    private static string Sign(string payload, string secret)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(payload));
        return Convert.ToHexStringLower(hash);
    }
}

internal sealed class PlatformPaymentProviderRegistry : IPlatformPaymentProviderRegistry
{
    private readonly Dictionary<string, IPlatformPaymentProvider> _providers;

    public PlatformPaymentProviderRegistry(IEnumerable<IPlatformPaymentProvider> providers)
    {
        _providers = providers.ToDictionary(p => p.Name, StringComparer.OrdinalIgnoreCase);
    }

    public IPlatformPaymentProvider Resolve(string name) =>
        TryResolve(name, out var provider)
            ? provider
            : throw new KeyNotFoundException($"No platform payment provider named '{name}' is configured.");

    public bool TryResolve(string name, out IPlatformPaymentProvider provider) =>
        _providers.TryGetValue(name ?? string.Empty, out provider!);
}
