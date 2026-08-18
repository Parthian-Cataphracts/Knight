using Knight.Domain.Common;
using Knight.Domain.Exceptions;

namespace Identity.Domain;

public enum SubjectType
{
    PlatformAdmin = 0,
    TenantUser = 1
}

/// <summary>
/// One revocable refresh token in a rotation family. Only the token hash is
/// persisted; the raw token value is never stored — see
/// docs/architecture/authorization.md. Every token issued from the same login
/// shares <see cref="FamilyId"/> and the same absolute <see cref="ExpiresAt"/>:
/// rotation never extends a family's lifetime, it only replaces which token
/// within it is currently valid.
/// </summary>
public sealed class RefreshToken : Entity
{
    public Guid SubjectId { get; private set; }

    public SubjectType SubjectType { get; private set; }

    /// <summary>Set only for tenant-bound families; null for platform-admin families.</summary>
    public Guid? TenantId { get; private set; }

    /// <summary>Shared by every token produced by rotating the same original login.</summary>
    public Guid FamilyId { get; private set; }

    public string TokenHash { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    /// <summary>The family's absolute expiration — identical across every token in the family.</summary>
    public DateTimeOffset ExpiresAt { get; private set; }

    /// <summary>Set when this token was exchanged during rotation.</summary>
    public DateTimeOffset? ConsumedAt { get; private set; }

    /// <summary>The token that replaced this one, when rotated.</summary>
    public Guid? ReplacedByTokenId { get; private set; }

    public DateTimeOffset? RevokedAt { get; private set; }

    public string? RevokedReason { get; private set; }

    public bool IsActive(DateTimeOffset now) => RevokedAt is null && ConsumedAt is null && now < ExpiresAt;

    private RefreshToken()
    {
        TokenHash = string.Empty;
    }

    private RefreshToken(
        Guid id,
        Guid subjectId,
        SubjectType subjectType,
        Guid? tenantId,
        Guid familyId,
        string tokenHash,
        DateTimeOffset createdAt,
        DateTimeOffset expiresAt)
        : base(id)
    {
        SubjectId = subjectId;
        SubjectType = subjectType;
        TenantId = tenantId;
        FamilyId = familyId;
        TokenHash = tokenHash;
        CreatedAt = createdAt;
        ExpiresAt = expiresAt;
    }

    /// <summary>Starts a brand-new family (a fresh login).</summary>
    public static RefreshToken IssueNewFamily(
        Guid id,
        Guid subjectId,
        SubjectType subjectType,
        Guid? tenantId,
        string tokenHash,
        DateTimeOffset createdAt,
        TimeSpan familyLifetime)
    {
        Validate(subjectType, tenantId, tokenHash);
        return new RefreshToken(id, subjectId, subjectType, tenantId, Guid.NewGuid(), tokenHash, createdAt, createdAt.Add(familyLifetime));
    }

    /// <summary>Issues the next token in an existing family during rotation — the absolute expiration is carried over, never extended.</summary>
    public static RefreshToken IssueRotated(Guid id, RefreshToken previous, string tokenHash, DateTimeOffset createdAt)
    {
        Validate(previous.SubjectType, previous.TenantId, tokenHash);
        return new RefreshToken(id, previous.SubjectId, previous.SubjectType, previous.TenantId, previous.FamilyId, tokenHash, createdAt, previous.ExpiresAt);
    }

    public void Revoke(DateTimeOffset now, string reason)
    {
        if (RevokedAt is not null)
        {
            return;
        }

        RevokedAt = now;
        RevokedReason = reason;
    }

    private static void Validate(SubjectType subjectType, Guid? tenantId, string tokenHash)
    {
        if (string.IsNullOrWhiteSpace(tokenHash))
        {
            throw DomainException.Validation("Token hash is required.");
        }

        if (subjectType == SubjectType.TenantUser && tenantId is null)
        {
            throw DomainException.Validation("A tenant-user refresh token must be bound to a tenant.");
        }

        if (subjectType == SubjectType.PlatformAdmin && tenantId is not null)
        {
            throw DomainException.Validation("A platform-admin refresh token must not be bound to a tenant.");
        }
    }
}
