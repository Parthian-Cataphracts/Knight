using System.Security.Cryptography;
using System.Text;

namespace Knight.StoreAgent.Tests;

/// <summary>
/// The two checks that stand between this store and code it did not ask for.
///
/// The digest shape is asserted rather than assumed. The node reference store
/// spent three phases computing <c>sha256:&lt;hex&gt;</c> where KNIGHT publishes
/// bare hex, and could not have accepted a single real artifact — its own tests
/// agreed with it, because they were written from the same assumption. Nothing
/// here computes an expected value the same way the code under test does.
/// </summary>
public sealed class ArtifactVerifierTests
{
    private static readonly byte[] Artifact = Encoding.UTF8.GetBytes("a delivered feature, more or less");

    /// <summary>The digest as an independent implementation would write it.</summary>
    private static string ExpectedDigest()
    {
        var builder = new StringBuilder();

        foreach (var value in SHA256.HashData(Artifact))
        {
            builder.Append(value.ToString("x2"));
        }

        return builder.ToString();
    }

    [Fact]
    public void TheDigestIsBareLowercaseHex()
    {
        var digest = ArtifactVerifier.DigestOf(Artifact);

        Assert.Equal(64, digest.Length);
        Assert.DoesNotContain(':', digest);
        Assert.Equal(digest.ToLowerInvariant(), digest);
        Assert.Equal(ExpectedDigest(), digest);
    }

    [Fact]
    public void AMissingDigestIsRefusedRatherThanSkipped()
    {
        var refused = Assert.Throws<StepFailedException>(() => ArtifactVerifier.VerifyDigest(Artifact, null));

        Assert.Equal("digest.missing", refused.Code);
    }

    [Fact]
    public void AWrongDigestIsRefused()
    {
        var refused = Assert.Throws<StepFailedException>(() => ArtifactVerifier.VerifyDigest(Artifact, new string('0', 64)));

        Assert.Equal("digest.mismatch", refused.Code);
    }

    [Fact]
    public void ADigestIsComparedWithoutCaseOrSurroundingSpace()
    {
        // KNIGHT sends lowercase and always has. Tolerating the two ways a
        // digest gets mangled in transit costs nothing and turns a mystery into
        // a non-event.
        var digest = ArtifactVerifier.DigestOf(Artifact);

        Assert.Equal(digest, ArtifactVerifier.VerifyDigest(Artifact, $"  {digest.ToUpperInvariant()}  "));
    }

    private static (string Signature, string PublicKey) Sign(string digest)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);

        var signature = key.SignData(
            Encoding.ASCII.GetBytes(digest),
            HashAlgorithmName.SHA256,
            DSASignatureFormat.Rfc3279DerSequence);

        return (Convert.ToBase64String(signature), Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()));
    }

    [Fact]
    public void AGenuineSignatureOverTheDigestIsAccepted()
    {
        var digest = ArtifactVerifier.DigestOf(Artifact);
        var (signature, publicKey) = Sign(digest);

        ArtifactVerifier.VerifySignature(digest, signature, "dev", new Dictionary<string, string> { ["dev"] = publicKey });
    }

    [Fact]
    public void ASignatureByAKeyThisStoreDoesNotTrustIsRefused()
    {
        var digest = ArtifactVerifier.DigestOf(Artifact);
        var (signature, _) = Sign(digest);
        var (_, stranger) = Sign(digest);

        // The digest is right, the bytes are right, and the signature is real —
        // by the wrong key. This is the check that distinguishes "arrived
        // intact" from "KNIGHT published it".
        var refused = Assert.Throws<StepFailedException>(() =>
            ArtifactVerifier.VerifySignature(digest, signature, "dev", new Dictionary<string, string> { ["dev"] = stranger }));

        Assert.Equal("signature.invalid", refused.Code);
    }

    [Fact]
    public void AKeyIdThisStoreHasNeverHeardOfIsRefusedRatherThanFetched()
    {
        var digest = ArtifactVerifier.DigestOf(Artifact);
        var (signature, publicKey) = Sign(digest);

        // Fetching the key named by the message that carries the signature
        // would prove only that the message agrees with itself.
        var refused = Assert.Throws<StepFailedException>(() =>
            ArtifactVerifier.VerifySignature(digest, signature, "somebody-elses-key", new Dictionary<string, string> { ["dev"] = publicKey }));

        Assert.Equal("signature.unknown_key", refused.Code);
    }

    [Fact]
    public void AnUnsignedArtifactIsRefused()
    {
        var digest = ArtifactVerifier.DigestOf(Artifact);

        var refused = Assert.Throws<StepFailedException>(() =>
            ArtifactVerifier.VerifySignature(digest, "", "dev", new Dictionary<string, string>()));

        Assert.Equal("signature.missing", refused.Code);
    }

    [Fact]
    public void ASignatureOverADifferentDigestIsRefused()
    {
        var digest = ArtifactVerifier.DigestOf(Artifact);
        var (signature, publicKey) = Sign(digest);

        // The whole reason the digest is verified *before* the signature: a
        // signature checked against a digest nobody confirmed matches the bytes
        // proves that KNIGHT signed *a* digest and says nothing about the file.
        var refused = Assert.Throws<StepFailedException>(() =>
            ArtifactVerifier.VerifySignature(new string('a', 64), signature, "dev", new Dictionary<string, string> { ["dev"] = publicKey }));

        Assert.Equal("signature.invalid", refused.Code);
    }
}
