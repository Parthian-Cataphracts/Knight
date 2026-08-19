using System.Globalization;
using System.Security.Cryptography;
using Knight.Application.Abstractions.ControlPlane;

namespace Knight.Infrastructure.ControlPlane.Integration;

/// <summary>
/// Signs a request KNIGHT makes to a store, so the store can tell KNIGHT apart
/// from anyone else who found the URL.
///
/// A store's health payload lists its version, its dependencies and its
/// installed features. That is exactly the reconnaissance an attacker wants, so
/// the endpoint is authenticated rather than public
/// (docs/store-integration.md §5). The proof is an HMAC under the same per-store
/// key the store received in its handshake — no new secret, and nothing KNIGHT
/// has to store.
///
/// The canonical string is deliberately small and explicit: method, path,
/// timestamp, nonce. It is flat text for the same reason the entitlement
/// signature is — two languages will not agree on JSON, and a signature that
/// only sometimes verifies is worse than none.
/// </summary>
public static class StoreRequestSignature
{
    public const string Version = "1";

    public const string StoreHeader = "X-Knight-Store";
    public const string TimestampHeader = "X-Knight-Timestamp";
    public const string NonceHeader = "X-Knight-Nonce";
    public const string SignatureHeader = "X-Knight-Signature";
    public const string VersionHeader = "X-Knight-Signature-Version";

    public static HttpRequestMessage Sign(
        HttpMethod method,
        Uri url,
        Guid storeId,
        string environment,
        IStorePayloadSigner signer)
    {
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        var nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(12)).ToLowerInvariant();

        var request = new HttpRequestMessage(method, url);
        request.Headers.TryAddWithoutValidation(StoreHeader, storeId.ToString("D"));
        request.Headers.TryAddWithoutValidation(TimestampHeader, timestamp);
        request.Headers.TryAddWithoutValidation(NonceHeader, nonce);
        request.Headers.TryAddWithoutValidation(VersionHeader, Version);
        request.Headers.TryAddWithoutValidation(
            SignatureHeader,
            signer.Sign(storeId, environment, Canonicalise(method.Method, url.AbsolutePath, timestamp, nonce)));

        return request;
    }

    /// <summary>
    /// The exact string both sides hash. The path is the absolute path only:
    /// a proxy in front of the store may legitimately change the host, and
    /// binding the signature to it would break every store behind one.
    /// </summary>
    public static string Canonicalise(string method, string path, string timestamp, string nonce) =>
        $"knight-request|{Version}|{method.ToUpperInvariant()}|{path}|{timestamp}|{nonce}";
}
