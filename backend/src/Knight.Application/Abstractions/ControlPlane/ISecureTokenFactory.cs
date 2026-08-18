namespace Knight.Application.Abstractions.ControlPlane;

public sealed record GeneratedSecret(string RawValue, string Hash);

/// <summary>
/// Produces opaque, cryptographically random secrets and their storage hashes.
/// Used for refresh tokens and for store client secrets: in both cases the raw
/// value exists only at issue time and only the hash is persisted.
/// </summary>
public interface ISecureTokenFactory
{
    GeneratedSecret Generate();

    string Hash(string rawValue);
}
