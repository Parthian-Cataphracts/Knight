using System.Security.Cryptography;
using System.Text;
using Knight.Application.Abstractions.ControlPlane;
using Knight.Application.Abstractions.Security;
using Microsoft.Extensions.Options;

namespace Knight.Infrastructure.ControlPlane.Security;

/// <summary>
/// Options for artifact signing and the package store, bound from the
/// "FeatureArtifacts" section.
/// </summary>
public sealed class FeatureArtifactOptions
{
    public const string SectionName = "FeatureArtifacts";

    /// <summary>
    /// The id of the key that signs new artifacts. Recorded on every version so
    /// that revoking a key means yanking exactly what it signed (risks.md R21).
    /// </summary>
    public string ActiveKeyId { get; init; } = "dev";

    /// <summary>
    /// Signing keys by id, as base64 DER. The private half may be absent for a
    /// retired key — verification still needs the public half long after the key
    /// has stopped signing anything.
    /// </summary>
    public IDictionary<string, FeatureSigningKey> Keys { get; init; } = new Dictionary<string, FeatureSigningKey>();

    /// <summary>Where built artifacts live. A local directory in development; object storage in deployment.</summary>
    public string ArtifactRoot { get; init; } = "artifacts";

    /// <summary>
    /// Base address a store fetches artifacts from. Download URLs are minted
    /// against this and carry an expiry.
    /// </summary>
    public string? PublicBaseUrl { get; init; }
}

public sealed class FeatureSigningKey
{
    public string? PrivateKey { get; init; }

    public string PublicKey { get; init; } = string.Empty;
}

/// <summary>
/// Detached signatures over artifact digests, using ECDSA on P-256 with SHA-256.
///
/// The curve is a deliberate second choice. Ed25519 was the intent, but .NET 10
/// ships no Ed25519 primitive — only ML-DSA and SLH-DSA — and putting a
/// third-party crypto library on the path that decides whether code may be
/// installed into a customer's production store is a worse trade than using the
/// NIST curve both runtimes already have. Python's `cryptography` verifies
/// P-256 as readily as it verifies Ed25519, so the store side is unaffected, and
/// <see cref="IFeatureArtifactSigner"/> exists precisely so this can change
/// without anything above it noticing.
///
/// Signatures are DER-encoded (RFC 3279), which has to be said explicitly: .NET
/// defaults to the IEEE P1363 fixed-width r||s encoding while OpenSSL, Python's
/// `cryptography` and most other ecosystems produce DER. The two carry the same
/// signature and neither verifies the other, so the format is pinned here rather
/// than left to a default that only agrees with itself.
///
/// The signature is over the digest rather than over the artifact bytes, which is
/// the standard detached arrangement and matters operationally: a store verifying
/// a 40MB wheel hashes it once for the digest check and then verifies a short
/// signature, instead of streaming the whole artifact through a verifier twice.
///
/// Keys are configuration-backed here, which is honest about what this is: enough
/// for development and CI, and the shape a KMS-backed implementation will take
/// when custody moves (risks.md R21).
/// </summary>
internal sealed class EcdsaArtifactSigner : IFeatureArtifactSigner
{
    private readonly FeatureArtifactOptions _options;

    public EcdsaArtifactSigner(IOptions<FeatureArtifactOptions> options)
    {
        _options = options.Value;
    }

    public string ActiveKeyId => _options.ActiveKeyId;

    public string Sign(string artifactDigest)
    {
        if (!_options.Keys.TryGetValue(_options.ActiveKeyId, out var key) || string.IsNullOrWhiteSpace(key.PrivateKey))
        {
            throw new InvalidOperationException(
                $"No private key is configured for signing key '{_options.ActiveKeyId}'.");
        }

        using var algorithm = ECDsa.Create();
        algorithm.ImportPkcs8PrivateKey(Convert.FromBase64String(key.PrivateKey), out _);

        return Convert.ToBase64String(algorithm.SignData(
            Encoding.UTF8.GetBytes(Normalise(artifactDigest)),
            HashAlgorithmName.SHA256,
            DSASignatureFormat.Rfc3279DerSequence));
    }

    public bool Verify(string artifactDigest, string signature, string keyId)
    {
        if (string.IsNullOrWhiteSpace(signature) ||
            !_options.Keys.TryGetValue(keyId, out var key) ||
            string.IsNullOrWhiteSpace(key.PublicKey))
        {
            // An unknown key is a failed verification, never a pass. A version
            // signed by something this deployment cannot identify is exactly what
            // the check exists to stop.
            return false;
        }

        try
        {
            using var algorithm = ECDsa.Create();
            algorithm.ImportSubjectPublicKeyInfo(Convert.FromBase64String(key.PublicKey), out _);

            return algorithm.VerifyData(
                Encoding.UTF8.GetBytes(Normalise(artifactDigest)),
                Convert.FromBase64String(signature),
                HashAlgorithmName.SHA256,
                DSASignatureFormat.Rfc3279DerSequence);
        }
        catch (FormatException)
        {
            // A signature that is not even base64 is a failure, not an exception
            // for the caller to handle: publish should say "not valid", not 500.
            return false;
        }
        catch (CryptographicException)
        {
            return false;
        }
    }

    /// <summary>
    /// The exact bytes that get signed. Pinned to a lowercase, trimmed digest so
    /// that a signature made by the packaging tool verifies here regardless of how
    /// either side happened to spell the hex.
    /// </summary>
    private static string Normalise(string artifactDigest) => artifactDigest.Trim().ToLowerInvariant();
}

/// <summary>
/// Authenticated encryption for values KNIGHT has to be able to read back.
///
/// AES-GCM rather than CBC: the tag means a tampered payload fails to decrypt
/// instead of decrypting to something else. The nonce is random per call and
/// stored beside the ciphertext, because reusing a nonce under one key is the one
/// mistake GCM does not survive.
///
/// The stored form is versioned so that a future key rotation or algorithm change
/// can read what this one wrote, rather than needing every row migrated first.
/// </summary>
internal sealed class AesGcmSecretProtector : ISecretProtector
{
    private const string Prefix = "v1";
    private const int NonceLength = 12;
    private const int TagLength = 16;

    private readonly byte[] _key;

    public AesGcmSecretProtector(byte[] key)
    {
        if (key.Length is not (16 or 24 or 32))
        {
            throw new ArgumentException("A secret-protection key must be 128, 192 or 256 bits.", nameof(key));
        }

        _key = key;
    }

    public string Protect(string plaintext)
    {
        ArgumentNullException.ThrowIfNull(plaintext);

        var nonce = RandomNumberGenerator.GetBytes(NonceLength);
        var bytes = Encoding.UTF8.GetBytes(plaintext);
        var ciphertext = new byte[bytes.Length];
        var tag = new byte[TagLength];

        using var aes = new AesGcm(_key, TagLength);
        aes.Encrypt(nonce, bytes, ciphertext, tag);

        return $"{Prefix}.{Convert.ToBase64String(nonce)}.{Convert.ToBase64String(ciphertext)}.{Convert.ToBase64String(tag)}";
    }

    public string Unprotect(string protectedValue)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(protectedValue);

        var parts = protectedValue.Split('.');
        if (parts.Length != 4 || parts[0] != Prefix)
        {
            throw new CryptographicException("The protected value is not in a recognised format.");
        }

        var nonce = Convert.FromBase64String(parts[1]);
        var ciphertext = Convert.FromBase64String(parts[2]);
        var tag = Convert.FromBase64String(parts[3]);
        var plaintext = new byte[ciphertext.Length];

        using var aes = new AesGcm(_key, TagLength);

        // Throws on a bad tag, which is the point: a tampered payload must fail
        // loudly rather than decrypt to something plausible.
        aes.Decrypt(nonce, ciphertext, tag, plaintext);

        return Encoding.UTF8.GetString(plaintext);
    }
}

/// <summary>
/// A filesystem-backed package store.
///
/// Object storage is the deployment answer (risks.md §3 Q8), and this is the
/// same interface over a directory so that development and the integration
/// suite do not need a running MinIO. The download URL it mints still carries an
/// expiry, so the agent's fetch path is exercised the same way in both.
/// </summary>
internal sealed class FileSystemArtifactStore : IFeatureArtifactStore
{
    private readonly FeatureArtifactOptions _options;

    public FileSystemArtifactStore(IOptions<FeatureArtifactOptions> options)
    {
        _options = options.Value;
    }

    public async Task<FeatureArtifactMetadata?> FindAsync(string packageReference, CancellationToken cancellationToken)
    {
        var path = ResolvePath(packageReference);
        if (path is null || !File.Exists(path))
        {
            return null;
        }

        // The digest is computed from the bytes actually on disk rather than
        // trusted from the caller: the whole point of the publish check is to
        // compare what was uploaded against what was declared.
        await using var stream = File.OpenRead(path);
        var digest = await SHA256.HashDataAsync(stream, cancellationToken);

        return new FeatureArtifactMetadata(
            packageReference,
            Convert.ToHexString(digest).ToLowerInvariant(),
            new FileInfo(path).Length);
    }

    public Task<Uri> CreateDownloadUrlAsync(string packageReference, TimeSpan lifetime, CancellationToken cancellationToken)
    {
        var baseUrl = _options.PublicBaseUrl?.TrimEnd('/')
            ?? throw new InvalidOperationException(
                "FeatureArtifacts:PublicBaseUrl must be configured before artifacts can be delivered.");

        var expiresAt = DateTimeOffset.UtcNow.Add(lifetime).ToUnixTimeSeconds();

        return Task.FromResult(new Uri(
            $"{baseUrl}/{Uri.EscapeDataString(packageReference)}?expires={expiresAt}"));
    }

    /// <summary>
    /// Stores an uploaded package under a generated name and hashes what landed
    /// on disk.
    ///
    /// The name is generated rather than taken from the upload: the uploaded
    /// file name is untrusted text, and the reference it becomes is used as a
    /// path. Only the extension is carried across, and only from a short
    /// allow-list, so a package can still be recognised by whoever looks in the
    /// directory.
    /// </summary>
    public async Task<FeatureArtifactMetadata> SaveAsync(
        string fileName,
        Stream content,
        CancellationToken cancellationToken)
    {
        var root = Path.GetFullPath(_options.ArtifactRoot);
        Directory.CreateDirectory(root);

        var extension = Path.GetExtension(fileName)?.ToLowerInvariant() switch
        {
            ".whl" => ".whl",
            ".zip" => ".zip",
            ".gz" => ".tar.gz",
            _ => ".zip",
        };

        var reference = $"{Guid.CreateVersion7():n}{extension}";
        var path = Path.Combine(root, reference);

        await using (var file = File.Create(path))
        {
            await content.CopyToAsync(file, cancellationToken);
        }

        await using var stored = File.OpenRead(path);
        var digest = await SHA256.HashDataAsync(stored, cancellationToken);

        return new FeatureArtifactMetadata(
            reference,
            Convert.ToHexString(digest).ToLowerInvariant(),
            new FileInfo(path).Length);
    }

    /// <summary>
    /// Resolves a package reference to a path under the artifact root, refusing
    /// anything that escapes it.
    ///
    /// The reference arrives from a publish request, so it is untrusted input
    /// being turned into a file path — the one place a "../" is worth being
    /// pedantic about.
    /// </summary>
    private string? ResolvePath(string packageReference)
    {
        if (string.IsNullOrWhiteSpace(packageReference) || packageReference.Contains("..", StringComparison.Ordinal))
        {
            return null;
        }

        var root = Path.GetFullPath(_options.ArtifactRoot);
        var candidate = Path.GetFullPath(Path.Combine(root, packageReference));

        return candidate.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal) ? candidate : null;
    }
}
