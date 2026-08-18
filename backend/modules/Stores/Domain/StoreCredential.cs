using Knight.Domain.Common;
using Knight.Domain.Exceptions;

namespace Stores.Domain;

/// <summary>
/// A store's integration credential. Only the hash of the secret is ever stored;
/// the plaintext is displayed once at issue time and never again
/// (docs/adr/0012-store-authentication-mechanism.md).
///
/// Rotation does not revoke immediately: the previous credential enters a grace
/// period so a running store keeps working until it picks up the new secret.
/// </summary>
public sealed class StoreCredential : Entity
{
    public Guid StoreId { get; private set; }

    public string ClientId { get; private set; }

    public string SecretHash { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    /// <summary>Set when the credential is superseded; it stays usable until then.</summary>
    public DateTimeOffset? ExpiresAt { get; private set; }

    public DateTimeOffset? RotatedAt { get; private set; }

    public DateTimeOffset? RevokedAt { get; private set; }

    public DateTimeOffset? LastUsedAt { get; private set; }

    private StoreCredential()
    {
        ClientId = string.Empty;
        SecretHash = string.Empty;
    }

    private StoreCredential(
        Guid id,
        Guid storeId,
        string clientId,
        string secretHash,
        DateTimeOffset createdAt,
        DateTimeOffset? expiresAt)
        : base(id)
    {
        StoreId = storeId;
        ClientId = clientId;
        SecretHash = secretHash;
        CreatedAt = createdAt;
        ExpiresAt = expiresAt;
    }

    internal static StoreCredential Issue(
        Guid id,
        Guid storeId,
        string clientId,
        string secretHash,
        DateTimeOffset createdAt,
        DateTimeOffset? expiresAt)
    {
        if (string.IsNullOrWhiteSpace(clientId))
        {
            throw DomainException.Validation("A credential requires a client id.");
        }

        if (string.IsNullOrWhiteSpace(secretHash))
        {
            throw DomainException.Validation("A credential requires a hashed secret.");
        }

        if (expiresAt is not null && expiresAt <= createdAt)
        {
            throw DomainException.Validation("A credential cannot expire before it is issued.");
        }

        return new StoreCredential(id, storeId, clientId.Trim(), secretHash, createdAt, expiresAt);
    }

    internal void BeginGracePeriod(DateTimeOffset expiresAt, DateTimeOffset now)
    {
        if (RevokedAt is not null)
        {
            throw DomainException.Conflict("A revoked credential cannot enter a grace period.");
        }

        RotatedAt = now;
        ExpiresAt = expiresAt;
    }

    internal void Revoke(DateTimeOffset now)
    {
        if (RevokedAt is not null)
        {
            throw DomainException.Conflict("The credential is already revoked.");
        }

        RevokedAt = now;
    }

    public void RecordUse(DateTimeOffset now)
    {
        if (!IsUsable(now))
        {
            throw DomainException.Conflict("An expired or revoked credential cannot be used.");
        }

        LastUsedAt = now;
    }

    /// <summary>
    /// A credential authenticates only while it is neither revoked nor past its
    /// expiry. Both conditions are evaluated against the caller's clock so tests
    /// and background jobs agree with request handling.
    /// </summary>
    public bool IsUsable(DateTimeOffset now) => RevokedAt is null && (ExpiresAt is null || ExpiresAt > now);

    public StoreCredentialState StateAt(DateTimeOffset now) => this switch
    {
        { RevokedAt: not null } => StoreCredentialState.Revoked,
        _ when ExpiresAt is not null && ExpiresAt <= now => StoreCredentialState.Expired,
        { RotatedAt: not null } => StoreCredentialState.GracePeriod,
        _ => StoreCredentialState.Active,
    };
}

public enum StoreCredentialState
{
    Active = 0,
    GracePeriod = 1,
    Expired = 2,
    Revoked = 3,
}
