using System.Security.Cryptography;
using System.Text;
using AccessControl.Abstractions;
using Knight.Application.Abstractions.ControlPlane;

namespace Knight.Infrastructure.ControlPlane.Security;

/// <summary>
/// The control plane's password hashing: PBKDF2-SHA256 with a per-password
/// random salt.
///
/// The output format is <c>{iterations}.{salt}.{hash}</c>, base64 segments, so
/// the work factor can be raised later without invalidating hashes already
/// issued — <see cref="NeedsRehash"/> is how an account gets upgraded on its next
/// successful sign-in rather than by a migration nobody can run.
///
/// This implementation and its format are carried over unchanged from the
/// legacy `Identity` module that phase 8 removed. Unchanged deliberately: every
/// existing account's hash is in this format, and a "cleaner" rewrite would lock
/// every one of them out.
/// </summary>
public sealed class ControlPlanePasswordHasher : IControlPlanePasswordHasher
{
    private const int SaltSizeBytes = 16;
    private const int KeySizeBytes = 32;
    private const int Iterations = 210_000;

    private static readonly HashAlgorithmName Algorithm = HashAlgorithmName.SHA256;

    public string Hash(string plaintextPassword)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(plaintextPassword);

        var salt = RandomNumberGenerator.GetBytes(SaltSizeBytes);
        var key = Rfc2898DeriveBytes.Pbkdf2(plaintextPassword, salt, Iterations, Algorithm, KeySizeBytes);

        return $"{Iterations}.{Convert.ToBase64String(salt)}.{Convert.ToBase64String(key)}";
    }

    public bool Verify(string plaintextPassword, string passwordHash)
    {
        var segments = passwordHash.Split('.', 3);

        if (segments.Length != 3 || !int.TryParse(segments[0], out var iterations))
        {
            return false;
        }

        byte[] salt;
        byte[] expectedKey;

        try
        {
            salt = Convert.FromBase64String(segments[1]);
            expectedKey = Convert.FromBase64String(segments[2]);
        }
        catch (FormatException)
        {
            // A malformed stored hash is a failed verification, not an
            // exception: it is attacker-influencable in principle, and throwing
            // here would turn a bad row into a 500 that says so.
            return false;
        }

        var actualKey = Rfc2898DeriveBytes.Pbkdf2(plaintextPassword, salt, iterations, Algorithm, expectedKey.Length);

        // Constant time, because a comparison that returned early would leak how
        // much of a guess was right.
        return CryptographicOperations.FixedTimeEquals(actualKey, expectedKey);
    }

    public bool NeedsRehash(string passwordHash)
    {
        var segments = passwordHash.Split('.', 3);

        return segments.Length != 3
            || !int.TryParse(segments[0], out var iterations)
            || iterations < Iterations;
    }
}

/// <summary>
/// Opaque secrets: 256 bits of randomness, returned once and stored only as a
/// SHA-256 hash.
///
/// Used for refresh tokens, store client secrets and agent provisioning tokens.
/// The hash is plain SHA-256 rather than PBKDF2 on purpose — these are
/// full-entropy random values, so there is nothing to brute-force and no reason
/// to make every verification expensive. That reasoning does not extend to
/// passwords, which is why they have their own hasher above.
/// </summary>
public sealed class SecureTokenFactory : ISecureTokenFactory
{
    private const int TokenSizeBytes = 32;

    public GeneratedSecret Generate()
    {
        var raw = Base64Url(RandomNumberGenerator.GetBytes(TokenSizeBytes));

        return new GeneratedSecret(raw, Hash(raw));
    }

    /// <summary>
    /// Base64 of the SHA-256 digest.
    ///
    /// The encoding is part of the stored format, not a detail: every refresh
    /// token, store client secret and agent provisioning token already in the
    /// database was hashed this way. Switching to hex here would not fail a
    /// build or a test — it would simply stop every existing credential from
    /// verifying, which is why it is spelled out rather than left to taste.
    /// </summary>
    public string Hash(string rawValue) =>
        Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(rawValue)));

    /// <summary>
    /// Base64url: these values travel in headers, URLs and configuration files,
    /// and a '+' or '/' in one is a support ticket waiting to happen.
    /// </summary>
    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes)
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');
}
