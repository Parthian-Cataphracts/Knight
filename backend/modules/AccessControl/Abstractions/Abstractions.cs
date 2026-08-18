using AccessControl.Domain;

namespace AccessControl.Abstractions;

/// <summary>
/// Hashes control-plane account passwords. Declared here rather than borrowed
/// from the legacy Identity module so the control plane owns its own contract;
/// Infrastructure adapts whichever key-derivation implementation is current.
/// Plaintext passwords are never persisted or logged.
/// </summary>
public interface IControlPlanePasswordHasher
{
    string Hash(string plaintextPassword);

    bool Verify(string plaintextPassword, string passwordHash);

    /// <summary>True when the stored hash used a weaker work factor than the current target.</summary>
    bool NeedsRehash(string passwordHash);
}

public sealed record IssuedAccessToken(string Token, DateTimeOffset ExpiresAt);

/// <summary>
/// Mints control-plane access tokens. Every token carries the principal type,
/// the session it belongs to and the environment it was minted in, so a token
/// from one environment cannot be replayed into another
/// (docs/authentication.md section 5).
/// </summary>
public interface IControlPlaneTokenGenerator
{
    IssuedAccessToken Issue(ControlPlaneUser user, UserSession session, IReadOnlyCollection<string> roleNames);
}

/// <summary>
/// Time-based one-time passwords (RFC 6238) for platform staff second factors.
/// </summary>
public interface ITotpService
{
    /// <summary>Generates a fresh base32 shared secret.</summary>
    string GenerateSecret();

    /// <summary>
    /// Verifies a code against the secret, accepting a small window either side
    /// of the current step so an authenticator with slight clock drift still works.
    /// </summary>
    bool Verify(string secret, string code, DateTimeOffset now);

    /// <summary>Builds the otpauth:// URI an authenticator app scans during enrolment.</summary>
    string BuildEnrollmentUri(string secret, string accountEmail, string issuer);
}
