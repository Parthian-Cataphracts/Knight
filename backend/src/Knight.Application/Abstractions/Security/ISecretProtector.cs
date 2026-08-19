namespace Knight.Application.Abstractions.Security;

/// <summary>
/// Encrypts values that KNIGHT must be able to read back.
///
/// This is deliberately not password hashing, and the distinction matters. A
/// password is verified, so it is hashed and never recovered. A feature's
/// configuration secret — an API key a customer pasted in — has to reach the
/// store that needs it, so it must be recoverable, which means it is encrypted
/// and the key becomes something KNIGHT is responsible for keeping
/// (docs/feature-delivery.md §9).
///
/// The interface is small on purpose: the only two operations are seal and open,
/// so there is no way for a caller to accidentally use a weaker mode or forget
/// the authentication tag.
/// </summary>
public interface ISecretProtector
{
    /// <summary>Encrypts a value, returning an opaque, self-describing string safe to store.</summary>
    string Protect(string plaintext);

    /// <summary>
    /// Decrypts a value produced by <see cref="Protect"/>. Throws if the payload
    /// has been tampered with rather than returning whatever the bytes decode to.
    /// </summary>
    string Unprotect(string protectedValue);
}
