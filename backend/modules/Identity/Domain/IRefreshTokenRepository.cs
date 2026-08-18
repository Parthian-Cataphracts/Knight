namespace Identity.Domain;

public enum RefreshTokenConsumeOutcome
{
    Consumed,
    AlreadyConsumedOrRevoked,
    NotFound
}

/// <summary>
/// Persistence contract for refresh-token rotation. The consume/rotate and
/// revoke-family operations are implemented as atomic, conditional database
/// operations (not read-then-write in application code) — see
/// docs/architecture/authorization.md for why this matters for concurrent
/// rotation and reuse detection.
/// </summary>
public interface IRefreshTokenRepository
{
    Task<RefreshToken?> GetByTokenHashAsync(string tokenHash, CancellationToken cancellationToken);

    Task AddAsync(RefreshToken token, CancellationToken cancellationToken);

    /// <summary>
    /// Atomically marks <paramref name="oldTokenId"/> as consumed (only if it is
    /// still active — not already consumed or revoked) and inserts
    /// <paramref name="newToken"/> in the same transaction. Returns
    /// <see cref="RefreshTokenConsumeOutcome.AlreadyConsumedOrRevoked"/> without
    /// inserting anything if another request already consumed or revoked it —
    /// this is what prevents a token from ever producing two valid descendants.
    /// </summary>
    Task<RefreshTokenConsumeOutcome> TryConsumeAndIssueAsync(Guid oldTokenId, RefreshToken newToken, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>Revokes every currently-active token sharing <paramref name="familyId"/>.</summary>
    Task<int> RevokeFamilyAsync(Guid familyId, DateTimeOffset now, string reason, CancellationToken cancellationToken);

    /// <summary>Revokes every currently-active token (across all families) for one subject.</summary>
    Task<int> RevokeAllActiveForSubjectAsync(SubjectType subjectType, Guid subjectId, DateTimeOffset now, string reason, CancellationToken cancellationToken);
}
