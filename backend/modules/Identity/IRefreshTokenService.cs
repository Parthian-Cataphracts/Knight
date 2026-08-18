using Identity.Domain;

namespace Identity;

public enum TokenRotationOutcome
{
    Success,
    NotFound,
    Reused,
    Expired,
    Revoked,
    ContextMismatch
}

public sealed record IssuedRefreshToken
{
    public required string RawToken { get; init; }
    public required DateTimeOffset ExpiresAt { get; init; }
    public required Guid FamilyId { get; init; }
}

public sealed record TokenRotationResult
{
    public required TokenRotationOutcome Outcome { get; init; }
    public IssuedRefreshToken? Token { get; init; }
    public Guid? SubjectId { get; init; }
    public Guid? TenantId { get; init; }

    public static TokenRotationResult Success(IssuedRefreshToken token, Guid subjectId, Guid? tenantId) =>
        new() { Outcome = TokenRotationOutcome.Success, Token = token, SubjectId = subjectId, TenantId = tenantId };

    public static TokenRotationResult Failure(TokenRotationOutcome outcome) => new() { Outcome = outcome };
}

/// <summary>
/// Owns the refresh-token rotation lifecycle: issuance, atomic single-use
/// rotation, reuse detection, and revocation. See
/// docs/architecture/authorization.md for the family/rotation model.
/// </summary>
public interface IRefreshTokenService
{
    Task<IssuedRefreshToken> IssueNewFamilyAsync(SubjectType subjectType, Guid subjectId, Guid? tenantId, CancellationToken cancellationToken);

    /// <summary>
    /// Atomically consumes <paramref name="rawToken"/> and issues its successor.
    /// <paramref name="expectedSubjectType"/> and <paramref name="expectedTenantId"/>
    /// must match the token's own context exactly — this is what prevents a
    /// platform refresh token from being used on the tenant refresh endpoint (or
    /// vice versa) and what enforces tenant binding on tenant refresh.
    /// </summary>
    Task<TokenRotationResult> RotateAsync(string rawToken, SubjectType expectedSubjectType, Guid? expectedTenantId, CancellationToken cancellationToken);

    /// <summary>Revokes the family owning <paramref name="rawToken"/>. Safe/idempotent if the token is unknown.</summary>
    Task RevokeByRawTokenAsync(string rawToken, string reason, CancellationToken cancellationToken);

    Task RevokeFamilyAsync(Guid familyId, string reason, CancellationToken cancellationToken);

    Task RevokeAllForSubjectAsync(SubjectType subjectType, Guid subjectId, string reason, CancellationToken cancellationToken);
}
