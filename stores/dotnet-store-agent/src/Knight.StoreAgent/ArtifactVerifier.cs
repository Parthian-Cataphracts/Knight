using System.Security.Cryptography;
using System.Text;

namespace Knight.StoreAgent;

/// <summary>A step refused what it was given. The code is KNIGHT's, and it is what gets reported.</summary>
public sealed class StepFailedException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

/// <summary>
/// Checking that a downloaded artifact is the one KNIGHT published.
///
/// Two checks, answering different questions. The <b>digest</b> answers "did
/// this arrive intact"; the <b>signature</b> answers "did KNIGHT publish it". A
/// store that checks only the first trusts whoever served the bytes, which for a
/// signed download URL is a bucket and a CDN.
///
/// The digest is computed from the bytes rather than taken from the payload.
/// Comparing the payload's digest to itself is a check that always passes.
///
/// ECDSA P-256 over the ASCII digest string, which is what
/// <c>knight_package.py</c> produces and what the Django and node reference
/// stores verify. The algorithm is not this store's choice — it is the contract,
/// and a store that verified some other way would accept artifacts KNIGHT never
/// signed.
/// </summary>
public static class ArtifactVerifier
{
    /// <summary>
    /// Bare lowercase hex, no algorithm prefix.
    ///
    /// The shape is the contract's. The node reference store spent three phases
    /// computing <c>sha256:&lt;hex&gt;</c> and could not have accepted a single
    /// real artifact; its own tests agreed with it because they were written
    /// from the same assumption.
    /// </summary>
    public static string DigestOf(ReadOnlySpan<byte> bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    public static string VerifyDigest(byte[] bytes, string? expected)
    {
        var wanted = (expected ?? string.Empty).Trim().ToLowerInvariant();

        if (wanted.Length == 0)
        {
            throw new StepFailedException("digest.missing", "The job names no digest to check the download against.");
        }

        var actual = DigestOf(bytes);

        // Fixed-time, not because a digest is a secret but because comparing
        // security-relevant values the sloppy way is a habit worth not having.
        if (!CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(actual),
                Encoding.ASCII.GetBytes(wanted)))
        {
            throw new StepFailedException(
                "digest.mismatch",
                $"The download hashes to {actual} and the job says {wanted}.");
        }

        return actual;
    }

    /// <summary>
    /// Confirms a key this store already trusts signed the digest.
    ///
    /// An unknown key id is a refusal, never a prompt to fetch one: fetching the
    /// key named by the message that carries the signature proves only that the
    /// message agrees with itself.
    /// </summary>
    public static void VerifySignature(
        string digest,
        string? signature,
        string? keyId,
        IReadOnlyDictionary<string, string> trustedKeys)
    {
        if (string.IsNullOrWhiteSpace(signature))
        {
            throw new StepFailedException("signature.missing", "The artifact carries no signature.");
        }

        if (string.IsNullOrWhiteSpace(keyId))
        {
            throw new StepFailedException("signature.no_key_id", "The artifact does not say which key signed it.");
        }

        if (!trustedKeys.TryGetValue(keyId, out var encoded) || string.IsNullOrWhiteSpace(encoded))
        {
            throw new StepFailedException(
                "signature.unknown_key",
                $"This store does not trust a signing key called '{keyId}'.");
        }

        using var key = ECDsa.Create();

        try
        {
            key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(encoded), out _);
        }
        catch (Exception exception) when (exception is FormatException or CryptographicException)
        {
            throw new StepFailedException(
                "signature.bad_key",
                $"The configured public key for '{keyId}' is not a base64 SubjectPublicKeyInfo.");
        }

        byte[] raw;

        try
        {
            raw = Convert.FromBase64String(signature);
        }
        catch (FormatException)
        {
            throw new StepFailedException("signature.malformed", "The signature is not valid base64.");
        }

        // DER, because that is what `cryptography` emits and what the other two
        // reference stores verify.
        if (!key.VerifyData(Encoding.ASCII.GetBytes(digest), raw, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence))
        {
            throw new StepFailedException(
                "signature.invalid",
                $"The signature over the artifact digest is not valid for key '{keyId}'.");
        }
    }
}
