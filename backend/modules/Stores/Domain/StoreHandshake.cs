using Knight.Domain.Exceptions;

namespace Stores.Domain;

/// <summary>
/// Why a handshake was refused. Returned rather than thrown because every one of
/// these is an expected outcome the ingestion endpoint must answer identically
/// to the caller — a store learns only that it was refused, never which check
/// failed, since that would tell an attacker which half of the credential to
/// keep working on (docs/authentication.md section 2).
/// </summary>
public enum HandshakeRefusal
{
    None = 0,

    /// <summary>No credential with that client id, or the secret did not match.</summary>
    UnknownCredential = 1,

    /// <summary>The credential is revoked or past its expiry.</summary>
    CredentialNotUsable = 2,

    /// <summary>The store is suspended or archived.</summary>
    StoreNotOperable = 3,

    /// <summary>
    /// The store reported an environment other than the one it is registered
    /// as. A production store must never report into a staging control plane,
    /// even with a valid credential.
    /// </summary>
    EnvironmentMismatch = 4,
}

public sealed record HandshakeResult(HandshakeRefusal Refusal, StoreCredential? Credential)
{
    public bool IsAccepted => Refusal is HandshakeRefusal.None;

    public static HandshakeResult Refused(HandshakeRefusal refusal) => new(refusal, null);
}

/// <summary>
/// The credential half of the store handshake, kept in the aggregate so the
/// order of the checks — and the fact that all of them run — cannot drift into
/// an endpoint.
/// </summary>
public static class StoreHandshake
{
    /// <summary>
    /// Verifies a presented credential against a store.
    ///
    /// The secret is compared by its hash through <paramref name="verifySecret"/>,
    /// so the plaintext never enters the domain, and the comparison stays with
    /// the hashing implementation that produced it.
    /// </summary>
    public static HandshakeResult Verify(
        Store store,
        string clientId,
        Func<string, bool> verifySecret,
        StoreEnvironment reportedEnvironment,
        DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(clientId))
        {
            throw DomainException.Validation("A handshake requires a client id.");
        }

        var credential = store.Credentials.SingleOrDefault(candidate => candidate.ClientId == clientId);
        if (credential is null || !verifySecret(credential.SecretHash))
        {
            return HandshakeResult.Refused(HandshakeRefusal.UnknownCredential);
        }

        if (!credential.IsUsable(now))
        {
            return HandshakeResult.Refused(HandshakeRefusal.CredentialNotUsable);
        }

        // A suspended store keeps its credentials but may not ingest: suspension
        // is a commercial decision that has to take effect immediately.
        if (store.Status is StoreStatus.Suspended or StoreStatus.Archived)
        {
            return HandshakeResult.Refused(HandshakeRefusal.StoreNotOperable);
        }

        if (reportedEnvironment != store.Environment)
        {
            return HandshakeResult.Refused(HandshakeRefusal.EnvironmentMismatch);
        }

        return new HandshakeResult(HandshakeRefusal.None, credential);
    }
}
