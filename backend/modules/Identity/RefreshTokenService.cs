using Identity.Abstractions;
using Identity.Domain;
using Identity.Options;
using Microsoft.Extensions.Options;
using Knight.Application.Abstractions.Auditing;
using Knight.Application.Abstractions.Time;

namespace Identity;

public sealed class RefreshTokenService : IRefreshTokenService
{
    private readonly IRefreshTokenRepository _repository;
    private readonly IRefreshTokenGenerator _generator;
    private readonly IDateTimeProvider _dateTimeProvider;
    private readonly IAuditLogger _auditLogger;
    private readonly RefreshTokenOptions _options;

    public RefreshTokenService(
        IRefreshTokenRepository repository,
        IRefreshTokenGenerator generator,
        IDateTimeProvider dateTimeProvider,
        IAuditLogger auditLogger,
        IOptions<RefreshTokenOptions> options)
    {
        _repository = repository;
        _generator = generator;
        _dateTimeProvider = dateTimeProvider;
        _auditLogger = auditLogger;
        _options = options.Value;
    }

    public async Task<IssuedRefreshToken> IssueNewFamilyAsync(SubjectType subjectType, Guid subjectId, Guid? tenantId, CancellationToken cancellationToken)
    {
        var now = _dateTimeProvider.UtcNow;
        var generated = _generator.Generate();
        var familyLifetime = subjectType == SubjectType.PlatformAdmin ? _options.PlatformFamilyLifetime : _options.TenantFamilyLifetime;

        var token = RefreshToken.IssueNewFamily(Guid.NewGuid(), subjectId, subjectType, tenantId, generated.TokenHash, now, familyLifetime);
        await _repository.AddAsync(token, cancellationToken);

        return new IssuedRefreshToken { RawToken = generated.RawToken, ExpiresAt = token.ExpiresAt, FamilyId = token.FamilyId };
    }

    public async Task<TokenRotationResult> RotateAsync(string rawToken, SubjectType expectedSubjectType, Guid? expectedTenantId, CancellationToken cancellationToken)
    {
        var now = _dateTimeProvider.UtcNow;
        var tokenHash = _generator.Hash(rawToken);

        var existing = await _repository.GetByTokenHashAsync(tokenHash, cancellationToken);
        if (existing is null)
        {
            return TokenRotationResult.Failure(TokenRotationOutcome.NotFound);
        }

        if (existing.SubjectType != expectedSubjectType || existing.TenantId != expectedTenantId)
        {
            // A token presented to the wrong context (platform vs tenant, or the
            // wrong tenant) is treated the same as "not found" externally, but is
            // worth its own internal signal — it is never a legitimate rotation.
            return TokenRotationResult.Failure(TokenRotationOutcome.ContextMismatch);
        }

        if (existing.RevokedAt is not null)
        {
            return TokenRotationResult.Failure(TokenRotationOutcome.Revoked);
        }

        if (existing.ConsumedAt is not null)
        {
            // Reuse of an already-rotated token: treat conservatively as
            // compromise and revoke the whole family, even though this also
            // means a benign concurrent-refresh race ends in required
            // reauthentication rather than a graceful single winner. See
            // docs/architecture/authorization.md.
            await _repository.RevokeFamilyAsync(existing.FamilyId, now, "refresh_token_reuse_detected", cancellationToken);

            await _auditLogger.RecordAsync(new AuditEntry
            {
                ActorUserId = existing.SubjectId,
                ActorType = existing.SubjectType == SubjectType.PlatformAdmin ? AuditActorType.PlatformAdmin : AuditActorType.TenantUser,
                TenantId = existing.TenantId,
                Action = "RefreshTokenReuseDetected",
                EntityType = nameof(RefreshToken),
                EntityId = existing.Id.ToString()
            }, cancellationToken);

            await _auditLogger.RecordAsync(new AuditEntry
            {
                ActorUserId = existing.SubjectId,
                ActorType = existing.SubjectType == SubjectType.PlatformAdmin ? AuditActorType.PlatformAdmin : AuditActorType.TenantUser,
                TenantId = existing.TenantId,
                Action = "RefreshFamilyRevoked",
                EntityType = nameof(RefreshToken),
                EntityId = existing.FamilyId.ToString()
            }, cancellationToken);

            return TokenRotationResult.Failure(TokenRotationOutcome.Reused);
        }

        if (now >= existing.ExpiresAt)
        {
            return TokenRotationResult.Failure(TokenRotationOutcome.Expired);
        }

        var generated = _generator.Generate();
        var rotated = RefreshToken.IssueRotated(Guid.NewGuid(), existing, generated.TokenHash, now);

        var consumeOutcome = await _repository.TryConsumeAndIssueAsync(existing.Id, rotated, now, cancellationToken);

        return consumeOutcome switch
        {
            RefreshTokenConsumeOutcome.Consumed => TokenRotationResult.Success(
                new IssuedRefreshToken { RawToken = generated.RawToken, ExpiresAt = rotated.ExpiresAt, FamilyId = rotated.FamilyId },
                existing.SubjectId,
                existing.TenantId),

            // Someone else consumed or revoked it between our read and our
            // conditional write — same conservative treatment as detected reuse.
            _ => await HandleLostRaceAsync(existing, now, cancellationToken)
        };
    }

    private async Task<TokenRotationResult> HandleLostRaceAsync(RefreshToken existing, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await _repository.RevokeFamilyAsync(existing.FamilyId, now, "concurrent_refresh_conflict", cancellationToken);

        await _auditLogger.RecordAsync(new AuditEntry
        {
            ActorUserId = existing.SubjectId,
            ActorType = existing.SubjectType == SubjectType.PlatformAdmin ? AuditActorType.PlatformAdmin : AuditActorType.TenantUser,
            TenantId = existing.TenantId,
            Action = "RefreshFamilyRevoked",
            EntityType = nameof(RefreshToken),
            EntityId = existing.FamilyId.ToString(),
            Metadata = new Dictionary<string, string> { ["reason"] = "concurrent_refresh_conflict" }
        }, cancellationToken);

        return TokenRotationResult.Failure(TokenRotationOutcome.Reused);
    }

    public async Task RevokeByRawTokenAsync(string rawToken, string reason, CancellationToken cancellationToken)
    {
        var tokenHash = _generator.Hash(rawToken);
        var existing = await _repository.GetByTokenHashAsync(tokenHash, cancellationToken);
        if (existing is null)
        {
            return;
        }

        await _repository.RevokeFamilyAsync(existing.FamilyId, _dateTimeProvider.UtcNow, reason, cancellationToken);
    }

    public Task RevokeAllForSubjectAsync(SubjectType subjectType, Guid subjectId, string reason, CancellationToken cancellationToken) =>
        _repository.RevokeAllActiveForSubjectAsync(subjectType, subjectId, _dateTimeProvider.UtcNow, reason, cancellationToken);

    public Task RevokeFamilyAsync(Guid familyId, string reason, CancellationToken cancellationToken) =>
        _repository.RevokeFamilyAsync(familyId, _dateTimeProvider.UtcNow, reason, cancellationToken);
}
