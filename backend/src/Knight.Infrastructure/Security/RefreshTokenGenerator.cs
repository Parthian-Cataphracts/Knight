using System.Security.Cryptography;
using Identity.Abstractions;

namespace Knight.Infrastructure.Security;

/// <summary>
/// Generates opaque refresh tokens: 256 bits of cryptographically random data,
/// base64url-encoded for transport. Only the SHA-256 hash of the raw value is
/// ever persisted — see docs/architecture/authorization.md.
/// </summary>
public sealed class RefreshTokenGenerator : IRefreshTokenGenerator
{
    private const int RawTokenSizeBytes = 32;

    public GeneratedRefreshToken Generate()
    {
        var raw = Base64UrlEncode(RandomNumberGenerator.GetBytes(RawTokenSizeBytes));
        return new GeneratedRefreshToken(raw, Hash(raw));
    }

    public string Hash(string rawToken) => Convert.ToBase64String(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(rawToken)));

    private static string Base64UrlEncode(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
