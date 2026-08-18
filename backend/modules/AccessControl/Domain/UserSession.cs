using Knight.Domain.Common;
using Knight.Domain.Exceptions;

namespace AccessControl.Domain;

/// <summary>
/// One refresh token in a rotation family. Only the hash is stored; the raw
/// token exists just long enough to be returned to the browser
/// (docs/authentication.md section 1).
///
/// Every token issued from the same login shares <see cref="FamilyId"/> and the
/// family's absolute <see cref="ExpiresAt"/>: rotation replaces which token is
/// currently valid, it never extends how long the login lasts. Presenting an
/// already-consumed token is the signature of a stolen refresh token, so the
/// whole family is revoked rather than just that one row.
/// </summary>
public sealed class UserSession : Entity, ICustomerScoped
{
    public Guid UserId { get; private set; }

    public Guid? CustomerId { get; private set; }

    public Guid FamilyId { get; private set; }

    public string RefreshTokenHash { get; private set; }

    public DateTimeOffset IssuedAt { get; private set; }

    /// <summary>The family's absolute expiry — identical on every token in the family.</summary>
    public DateTimeOffset ExpiresAt { get; private set; }

    /// <summary>Set when this token was exchanged during rotation.</summary>
    public DateTimeOffset? ConsumedAt { get; private set; }

    public Guid? ReplacedBySessionId { get; private set; }

    public DateTimeOffset? RevokedAt { get; private set; }

    public string? RevokedReason { get; private set; }

    /// <summary>True once the second factor has been satisfied for this login.</summary>
    public bool MfaSatisfied { get; private set; }

    public string? IpAddress { get; private set; }

    public string? UserAgent { get; private set; }

    private UserSession()
    {
        RefreshTokenHash = string.Empty;
    }

    private UserSession(
        Guid id,
        Guid userId,
        Guid? customerId,
        Guid familyId,
        string refreshTokenHash,
        DateTimeOffset issuedAt,
        DateTimeOffset expiresAt,
        bool mfaSatisfied,
        string? ipAddress,
        string? userAgent)
        : base(id)
    {
        UserId = userId;
        CustomerId = customerId;
        FamilyId = familyId;
        RefreshTokenHash = refreshTokenHash;
        IssuedAt = issuedAt;
        ExpiresAt = expiresAt;
        MfaSatisfied = mfaSatisfied;
        IpAddress = ipAddress;
        UserAgent = userAgent;
    }

    /// <summary>Starts a new family: a fresh login.</summary>
    public static UserSession StartFamily(
        Guid id,
        ControlPlaneUser user,
        string refreshTokenHash,
        DateTimeOffset issuedAt,
        TimeSpan familyLifetime,
        bool mfaSatisfied,
        string? ipAddress = null,
        string? userAgent = null)
    {
        if (string.IsNullOrWhiteSpace(refreshTokenHash))
        {
            throw DomainException.Validation("A refresh token hash is required.");
        }

        if (familyLifetime <= TimeSpan.Zero)
        {
            throw DomainException.Validation("A session lifetime must be positive.");
        }

        return new UserSession(
            id,
            user.Id,
            user.CustomerId,
            Guid.NewGuid(),
            refreshTokenHash,
            issuedAt,
            issuedAt.Add(familyLifetime),
            mfaSatisfied,
            Truncate(ipAddress, 64),
            Truncate(userAgent, 512));
    }

    /// <summary>Issues the next token in this family, carrying the absolute expiry over unchanged.</summary>
    public UserSession Rotate(Guid replacementId, string refreshTokenHash, DateTimeOffset now)
    {
        if (!IsActive(now))
        {
            throw DomainException.Conflict("Only an active session can be rotated.");
        }

        if (string.IsNullOrWhiteSpace(refreshTokenHash))
        {
            throw DomainException.Validation("A refresh token hash is required.");
        }

        var replacement = new UserSession(
            replacementId,
            UserId,
            CustomerId,
            FamilyId,
            refreshTokenHash,
            now,
            ExpiresAt,
            MfaSatisfied,
            IpAddress,
            UserAgent);

        ConsumedAt = now;
        ReplacedBySessionId = replacementId;
        return replacement;
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

    /// <summary>Records that the second factor was satisfied after the initial password step.</summary>
    public void MarkMfaSatisfied()
    {
        MfaSatisfied = true;
    }

    public bool IsActive(DateTimeOffset now) => RevokedAt is null && ConsumedAt is null && now < ExpiresAt;

    /// <summary>
    /// True when this token has already been exchanged. A caller presenting one
    /// is either replaying an old value or holding a stolen copy; either way the
    /// family is no longer trustworthy.
    /// </summary>
    public bool IsReplay(DateTimeOffset now) => RevokedAt is null && ConsumedAt is not null && now < ExpiresAt;

    private static string? Truncate(string? value, int maxLength) =>
        string.IsNullOrWhiteSpace(value)
            ? null
            : value.Length <= maxLength ? value : value[..maxLength];
}
