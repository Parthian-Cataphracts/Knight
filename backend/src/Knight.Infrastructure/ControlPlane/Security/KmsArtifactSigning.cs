using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Knight.Application.Abstractions.ControlPlane;
using Knight.Application.Abstractions.Security;
using Microsoft.Extensions.Options;

namespace Knight.Infrastructure.ControlPlane.Security;

/// <summary>
/// The artifact signer whose private key is not in this process (hardening
/// backlog P0). Signing is delegated to an <see cref="IKmsSigner"/> — a cloud KMS,
/// an HSM, or an internal signing service — so a leaked configuration file or CI
/// secret no longer leaks the key that decides whether code may be installed into
/// a customer's store.
///
/// Verification stays local and is byte-for-byte the same as the config-backed
/// signer's, through <see cref="ArtifactSignatureCodec"/>: the public key is not a
/// secret, and a store checking a signature must not depend on the KMS being
/// reachable.
/// </summary>
internal sealed class KmsArtifactSigner : IFeatureArtifactSigner
{
    private readonly IKmsSigner _kms;
    private readonly FeatureArtifactOptions _options;

    public KmsArtifactSigner(IKmsSigner kms, IOptions<FeatureArtifactOptions> options)
    {
        _kms = kms;
        _options = options.Value;
    }

    public string ActiveKeyId => _options.ActiveKeyId;

    public string Sign(string artifactDigest)
    {
        // Signing is a publish-time act, not a hot path, so blocking on the KMS
        // call keeps the synchronous IFeatureArtifactSigner contract while the
        // private key stays where it is.
        return _kms
            .SignAsync(ActiveKeyId, ArtifactSignatureCodec.SigningBytes(artifactDigest), CancellationToken.None)
            .GetAwaiter()
            .GetResult();
    }

    public bool Verify(string artifactDigest, string signature, string keyId)
    {
        if (!_options.Keys.TryGetValue(keyId, out var key))
        {
            // An unknown key is a failed verification, never a pass — the same
            // rule the config signer keeps.
            return false;
        }

        return ArtifactSignatureCodec.Verify(artifactDigest, signature, key.PublicKey);
    }
}

/// <summary>
/// An <see cref="IKmsSigner"/> over a small JSON signing service: POST
/// <c>{ keyId, message, algorithm }</c>, receive <c>{ signature }</c>. It fits an
/// internal KMS proxy or Vault Transit, and keeps this codebase free of a vendor
/// SDK on the path that decides installability. A first-party SDK adapter (AWS
/// KMS, Azure Key Vault) is an alternative <see cref="IKmsSigner"/> and needs no
/// change above it.
/// </summary>
internal sealed class HttpKmsSigner : IKmsSigner
{
    private readonly HttpClient _http;
    private readonly KmsOptions _options;

    public HttpKmsSigner(HttpClient http, IOptions<FeatureArtifactOptions> options)
    {
        _http = http;
        _options = options.Value.Kms;
    }

    public async Task<string> SignAsync(string keyId, byte[] data, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_options.Endpoint))
        {
            throw new InvalidOperationException("No KMS endpoint is configured; the 'kms' signer cannot sign.");
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, _options.Endpoint)
        {
            Content = JsonContent.Create(new KmsSignRequest(keyId, Convert.ToBase64String(data), "ECDSA_P256_SHA256")),
        };

        if (!string.IsNullOrWhiteSpace(_options.Token))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.Token);
        }

        using var response = await _http.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"The KMS signing service refused to sign ({(int)response.StatusCode}).");
        }

        var result = await response.Content.ReadFromJsonAsync<KmsSignResponse>(cancellationToken);
        if (result is null || string.IsNullOrWhiteSpace(result.Signature))
        {
            throw new InvalidOperationException("The KMS signing service returned no signature.");
        }

        return result.Signature;
    }

    private sealed record KmsSignRequest(
        [property: JsonPropertyName("keyId")] string KeyId,
        [property: JsonPropertyName("message")] string Message,
        [property: JsonPropertyName("algorithm")] string Algorithm);

    private sealed record KmsSignResponse([property: JsonPropertyName("signature")] string? Signature);
}
