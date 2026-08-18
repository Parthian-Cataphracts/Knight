using System.Security.Cryptography;
using Identity.Abstractions;

namespace Knight.Infrastructure.Security;

/// <summary>
/// PBKDF2-based password hasher using a per-password random salt. The output
/// format is "{iterations}.{salt}.{hash}" (base64 segments) so the work factor
/// can evolve without invalidating previously issued hashes.
/// </summary>
public sealed class Pbkdf2PasswordHasher : IPasswordHasher
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

        var salt = Convert.FromBase64String(segments[1]);
        var expectedKey = Convert.FromBase64String(segments[2]);

        var actualKey = Rfc2898DeriveBytes.Pbkdf2(plaintextPassword, salt, iterations, Algorithm, expectedKey.Length);

        return CryptographicOperations.FixedTimeEquals(actualKey, expectedKey);
    }

    public bool NeedsRehash(string passwordHash)
    {
        var segments = passwordHash.Split('.', 3);
        return segments.Length != 3 || !int.TryParse(segments[0], out var iterations) || iterations < Iterations;
    }
}
