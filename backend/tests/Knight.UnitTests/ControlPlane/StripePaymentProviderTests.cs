using System.Net;
using System.Security.Cryptography;
using System.Text;
using Knight.Application.Abstractions.Time;
using Knight.Domain.Common;
using Microsoft.Extensions.Options;
using NSubstitute;
using PlatformBilling;
using PlatformBilling.Domain;
using PlatformBilling.Payments;
using Xunit;

namespace Knight.UnitTests.ControlPlane;

/// <summary>
/// The Stripe adapter's security-critical half — the webhook. Verification is
/// Stripe's own scheme, so it is pinned here without reaching Stripe: a valid
/// signature verifies, a wrong one does not, and a stale timestamp is refused so a
/// captured callback cannot be replayed (docs/self-service-saas-plan.md §11).
/// </summary>
public sealed class StripePaymentProviderTests
{
    private static readonly DateTimeOffset Now = new(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);
    private const string WebhookSecret = "whsec_test_secret";

    private readonly IDateTimeProvider _clock = Substitute.For<IDateTimeProvider>();

    public StripePaymentProviderTests()
    {
        _clock.UtcNow.Returns(Now);
    }

    private StripePaymentProvider Build(HttpMessageHandler? handler = null, string secretKey = "sk_test")
    {
        var options = Options.Create(new PlatformBillingOptions
        {
            Stripe = new StripeOptions { SecretKey = secretKey, WebhookSecret = WebhookSecret, SignatureToleranceSeconds = 300 },
        });

        var http = new HttpClient(handler ?? new StubHandler(HttpStatusCode.OK, "{}"));
        return new StripePaymentProvider(http, options, _clock);
    }

    private static string SignatureHeader(string payload, long timestamp, string secret = WebhookSecret)
    {
        var signed = $"{timestamp}.{payload}";
        var v1 = Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(signed)));
        return $"t={timestamp},v1={v1}";
    }

    [Fact]
    public void AValidSignatureVerifies()
    {
        var provider = Build();
        const string payload = """{"type":"checkout.session.completed"}""";
        var header = SignatureHeader(payload, Now.ToUnixTimeSeconds());

        Assert.True(provider.VerifySignature(payload, header));
    }

    [Fact]
    public void AWrongSignatureIsRejected()
    {
        var provider = Build();
        const string payload = """{"type":"checkout.session.completed"}""";
        var header = $"t={Now.ToUnixTimeSeconds()},v1=deadbeef";

        Assert.False(provider.VerifySignature(payload, header));
    }

    [Fact]
    public void ASignatureSignedWithADifferentSecretIsRejected()
    {
        var provider = Build();
        const string payload = """{"type":"checkout.session.completed"}""";
        var header = SignatureHeader(payload, Now.ToUnixTimeSeconds(), secret: "whsec_someone_elses_secret");

        Assert.False(provider.VerifySignature(payload, header));
    }

    [Fact]
    public void AStaleTimestampIsRejected()
    {
        var provider = Build();
        const string payload = """{"type":"checkout.session.completed"}""";
        // Ten minutes old, tolerance is five.
        var header = SignatureHeader(payload, Now.AddMinutes(-10).ToUnixTimeSeconds());

        Assert.False(provider.VerifySignature(payload, header));
    }

    [Fact]
    public void NoSignatureHeaderIsRejected()
    {
        Assert.False(Build().VerifySignature("{}", signature: null));
    }

    [Fact]
    public void ACompletedCheckoutParsesToASucceededPayment()
    {
        var provider = Build();
        const string payload = """
            {"type":"checkout.session.completed","data":{"object":{"id":"cs_test_123","payment_intent":"pi_test_456"}}}
            """;

        Assert.True(provider.TryParseEvent(payload, out var evt));
        Assert.Equal(PlatformPaymentEventKind.PaymentSucceeded, evt.Kind);
        Assert.Equal("cs_test_123", evt.ProviderSessionId);
        Assert.Equal("pi_test_456", evt.ProviderTransactionId);
    }

    [Fact]
    public void AFailedAsyncPaymentParsesToAFailedPayment()
    {
        var provider = Build();
        const string payload = """
            {"type":"checkout.session.async_payment_failed","data":{"object":{"id":"cs_test_123"}}}
            """;

        Assert.True(provider.TryParseEvent(payload, out var evt));
        Assert.Equal(PlatformPaymentEventKind.PaymentFailed, evt.Kind);
    }

    [Fact]
    public void AnUnrelatedEventParsesAsUnhandled()
    {
        var provider = Build();
        const string payload = """
            {"type":"customer.created","data":{"object":{"id":"cus_1"}}}
            """;

        Assert.True(provider.TryParseEvent(payload, out var evt));
        Assert.Equal(PlatformPaymentEventKind.Unhandled, evt.Kind);
    }

    [Fact]
    public async Task StartCheckoutCallsStripeAndReturnsItsUrlAndSessionId()
    {
        var handler = new StubHandler(HttpStatusCode.OK,
            """{"id":"cs_live_789","url":"https://checkout.stripe.com/pay/cs_live_789"}""");
        var provider = Build(handler);

        var session = CheckoutSession.Open(
            Guid.CreateVersion7(), Now, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            BillingInterval.Monthly, [], Money.Of(49m, "EUR"), Now.AddHours(1));

        var start = await provider.StartCheckoutAsync(session, CancellationToken.None);

        Assert.Equal("cs_live_789", start.ProviderSessionId);
        Assert.StartsWith("https://checkout.stripe.com/", start.CheckoutUrl);

        // The request carried KNIGHT's own session id and the amount in cents.
        // (The keys are percent-encoded by form encoding; the values are what matter.)
        Assert.Contains($"client_reference_id={session.Id:n}", handler.LastBody, StringComparison.Ordinal);
        Assert.Contains("=4900", handler.LastBody, StringComparison.Ordinal);
        Assert.Equal("Bearer sk_test", handler.LastAuthorization);
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;
        private readonly string _body;

        public StubHandler(HttpStatusCode status, string body)
        {
            _status = status;
            _body = body;
        }

        public string LastBody { get; private set; } = string.Empty;

        public string? LastAuthorization { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastBody = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            LastAuthorization = request.Headers.Authorization?.ToString();

            return new HttpResponseMessage(_status) { Content = new StringContent(_body) };
        }
    }
}
