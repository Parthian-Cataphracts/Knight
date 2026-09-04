namespace Knight.Application.Abstractions.Security;

/// <summary>
/// External custody of a signing key: signing happens where the private key
/// lives — a cloud KMS, an HSM, or an internal signing service — and the key
/// never enters this process (risks.md R21, the post-Phase-30 hardening backlog
/// P0).
///
/// This is the seam the KMS-backed <c>IFeatureArtifactSigner</c> stands on. It
/// signs only; verification stays in-process against the public key, which is not
/// a secret. The signature it returns is base64, ECDSA on P-256 with SHA-256, in
/// the DER (RFC 3279) sequence encoding the verifier already expects — so moving
/// custody to a KMS changes where the key is, not what a store checks.
/// </summary>
public interface IKmsSigner
{
    /// <summary>
    /// Signs the exact bytes the verifier will check, with the named key, in the
    /// external key store. Returns a base64 detached signature.
    /// </summary>
    Task<string> SignAsync(string keyId, byte[] data, CancellationToken cancellationToken);
}
