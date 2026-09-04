using System.Net;
using System.Security.Cryptography;
using Knight.Application.Abstractions.Security;
using Knight.Infrastructure.ControlPlane.Security;
using Microsoft.Extensions.Options;
using Xunit;

namespace Knight.UnitTests.ControlPlane;

/// <summary>
/// The KMS-backed artifact signer (hardening backlog P0): signing is delegated to
/// an external key store, and the private key never enters the process — the
/// options here carry only the public half. Verification is local and byte-for-byte
/// the config signer's, so an artifact signed via KMS verifies exactly as one
/// signed from a file.
/// </summary>
public sealed class KmsArtifactSignerTests
{
    private const string Digest = "3b1a2c4d5e6f70819293a4b5c6d7e8f90112233445566778899aabbccddeeff00";

    private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly IOptions<FeatureArtifactOptions> _options;
    private readonly KmsArtifactSigner _signer;

    public KmsArtifactSignerTests()
    {
        var publicKey = Convert.ToBase64String(_key.ExportSubjectPublicKeyInfo());

        _options = Options.Create(new FeatureArtifactOptions
        {
            ActiveKeyId = "k1",
            // Only the public half — the private key lives in the "KMS".
            Keys = new Dictionary<string, FeatureSigningKey>
            {
                ["k1"] = new() { PublicKey = publicKey },
            },
        });

        _signer = new KmsArtifactSigner(new InProcessKms(_key), _options);
    }

    [Fact]
    public void ASignatureMadeViaTheKmsVerifiesLocally()
    {
        var signature = _signer.Sign(Digest);

        Assert.True(_signer.Verify(Digest, signature, "k1"));
    }

    [Fact]
    public void ASignatureOverADifferentDigestDoesNotVerify()
    {
        var signature = _signer.Sign(Digest);

        Assert.False(_signer.Verify(new string('a', 64), signature, "k1"));
    }

    [Fact]
    public void AnUnknownKeyNeverVerifies()
    {
        var signature = _signer.Sign(Digest);

        Assert.False(_signer.Verify(Digest, signature, "no-such-key"));
    }

    [Fact]
    public void SigningDoesNotNeedThePrivateKeyInConfiguration()
    {
        // The options hold no private key at all; signing still works because it
        // goes to the KMS. This is the whole point of the P0 change.
        Assert.Null(_options.Value.Keys["k1"].PrivateKey);
        Assert.False(string.IsNullOrEmpty(_signer.Sign(Digest)));
    }

    /// <summary>Stands in for a KMS: it holds the private key and signs with it, returning DER exactly as a KMS would.</summary>
    private sealed class InProcessKms : IKmsSigner
    {
        private readonly ECDsa _key;

        public InProcessKms(ECDsa key) => _key = key;

        public Task<string> SignAsync(string keyId, byte[] data, CancellationToken cancellationToken) =>
            Task.FromResult(Convert.ToBase64String(
                _key.SignData(data, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence)));
    }
}

/// <summary>
/// The HTTP signing client: it POSTs the message and key to the configured service
/// and returns the signature the service produced, so the process never holds the
/// private key.
/// </summary>
public sealed class HttpKmsSignerTests
{
    [Fact]
    public async Task ItPostsTheMessageAndReturnsTheSignature()
    {
        var handler = new StubHandler(HttpStatusCode.OK, """{"signature":"c2ln"}""");
        var options = Options.Create(new FeatureArtifactOptions
        {
            Kms = new KmsOptions { Endpoint = "https://kms.internal/sign", Token = "kms-token" },
        });

        var signer = new HttpKmsSigner(new HttpClient(handler), options);

        var signature = await signer.SignAsync("k1", "hello"u8.ToArray(), CancellationToken.None);

        Assert.Equal("c2ln", signature);
        Assert.Contains("\"keyId\":\"k1\"", handler.LastBody, StringComparison.Ordinal);
        Assert.Contains("\"message\":\"aGVsbG8=\"", handler.LastBody, StringComparison.Ordinal); // base64 of "hello"
        Assert.Contains("ECDSA_P256_SHA256", handler.LastBody, StringComparison.Ordinal);
        Assert.Equal("Bearer kms-token", handler.LastAuthorization);
    }

    [Fact]
    public async Task ARefusalIsAnError()
    {
        var handler = new StubHandler(HttpStatusCode.Forbidden, "no");
        var options = Options.Create(new FeatureArtifactOptions { Kms = new KmsOptions { Endpoint = "https://kms.internal/sign" } });
        var signer = new HttpKmsSigner(new HttpClient(handler), options);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            signer.SignAsync("k1", [1, 2, 3], CancellationToken.None));
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
