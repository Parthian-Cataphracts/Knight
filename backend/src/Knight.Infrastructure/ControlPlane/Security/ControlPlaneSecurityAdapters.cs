using AccessControl.Abstractions;
using Identity.Abstractions;
using Knight.Application.Abstractions.ControlPlane;

namespace Knight.Infrastructure.ControlPlane.Security;

/// <summary>
/// Adapts the existing PBKDF2 hasher to the control plane's own contract. The
/// algorithm is shared deliberately — there is one password-hashing decision in
/// this system, not two — while the interface stays owned by the control plane
/// so the legacy module can be removed in phase 8 without touching it.
/// </summary>
public sealed class ControlPlanePasswordHasher : IControlPlanePasswordHasher
{
    private readonly IPasswordHasher _inner;

    public ControlPlanePasswordHasher(IPasswordHasher inner)
    {
        _inner = inner;
    }

    public string Hash(string plaintextPassword) => _inner.Hash(plaintextPassword);

    public bool Verify(string plaintextPassword, string passwordHash) => _inner.Verify(plaintextPassword, passwordHash);

    public bool NeedsRehash(string passwordHash) => _inner.NeedsRehash(passwordHash);
}

/// <summary>
/// Adapts the existing opaque-token generator: 256 bits of randomness, stored
/// only as a SHA-256 hash. Used for refresh tokens and store client secrets.
/// </summary>
public sealed class SecureTokenFactory : ISecureTokenFactory
{
    private readonly IRefreshTokenGenerator _inner;

    public SecureTokenFactory(IRefreshTokenGenerator inner)
    {
        _inner = inner;
    }

    public GeneratedSecret Generate()
    {
        var generated = _inner.Generate();
        return new GeneratedSecret(generated.RawToken, generated.TokenHash);
    }

    public string Hash(string rawValue) => _inner.Hash(rawValue);
}
