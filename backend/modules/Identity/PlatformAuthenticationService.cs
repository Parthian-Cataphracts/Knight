using Identity.Abstractions;
using Identity.Authentication;
using Identity.Domain;
using Identity.Options;
using Microsoft.Extensions.Options;
using Knight.Application.Abstractions.Auditing;
using Knight.Application.Abstractions.Time;

namespace Identity;

public sealed class PlatformAuthenticationService : IPlatformAuthenticationService
{
    private readonly IPlatformAdminRepository _repository;
    private readonly IPasswordHasher _passwordHasher;
    private readonly IAccessTokenGenerator _accessTokenGenerator;
    private readonly IRefreshTokenService _refreshTokenService;
    private readonly IDateTimeProvider _dateTimeProvider;
    private readonly IAuditLogger _auditLogger;
    private readonly PasswordPolicyOptions _passwordPolicy;
    private readonly CredentialVerifier _credentialVerifier;

    public PlatformAuthenticationService(
        IPlatformAdminRepository repository,
        IPasswordHasher passwordHasher,
        IAccessTokenGenerator accessTokenGenerator,
        IRefreshTokenService refreshTokenService,
        IDateTimeProvider dateTimeProvider,
        IAuditLogger auditLogger,
        IOptions<LockoutOptions> lockoutOptions,
        IOptions<PasswordPolicyOptions> passwordPolicyOptions)
    {
        _repository = repository;
        _passwordHasher = passwordHasher;
        _accessTokenGenerator = accessTokenGenerator;
        _refreshTokenService = refreshTokenService;
        _dateTimeProvider = dateTimeProvider;
        _auditLogger = auditLogger;
        _passwordPolicy = passwordPolicyOptions.Value;
        _credentialVerifier = new CredentialVerifier(passwordHasher, lockoutOptions.Value);
    }

    public async Task<LoginResult> LoginAsync(string email, string password, CancellationToken cancellationToken)
    {
        var now = _dateTimeProvider.UtcNow;
        var normalizedEmail = NormalizeForLookup(email);
        var admin = await _repository.GetByNormalizedEmailAsync(normalizedEmail, cancellationToken);

        var outcome = _credentialVerifier.Verify(admin, password, now);

        if (admin is not null)
        {
            var rehash = outcome == LoginOutcome.Success ? _credentialVerifier.RehashIfNeeded(admin.PasswordHash, password) : null;
            if (rehash is not null)
            {
                admin.ChangePasswordHash(rehash, now);
            }

            await _repository.SaveChangesAsync(cancellationToken);
        }

        if (outcome != LoginOutcome.Success || admin is null)
        {
            if (outcome == LoginOutcome.AccountLocked && admin is not null)
            {
                await _auditLogger.RecordAsync(new AuditEntry
                {
                    ActorUserId = admin.Id,
                    ActorType = AuditActorType.PlatformAdmin,
                    Action = "AccountLocked",
                    EntityType = nameof(PlatformAdmin),
                    EntityId = admin.Id.ToString()
                }, cancellationToken);
            }

            return LoginResult.Failure(outcome);
        }

        var accessToken = _accessTokenGenerator.GenerateForPlatformAdmin(admin);
        var refreshToken = await _refreshTokenService.IssueNewFamilyAsync(SubjectType.PlatformAdmin, admin.Id, tenantId: null, cancellationToken);

        await _auditLogger.RecordAsync(new AuditEntry
        {
            ActorUserId = admin.Id,
            ActorType = AuditActorType.PlatformAdmin,
            Action = "PlatformLoginSucceeded",
            EntityType = nameof(PlatformAdmin),
            EntityId = admin.Id.ToString()
        }, cancellationToken);

        return LoginResult.Success(ToSession(accessToken, refreshToken), admin.Id);
    }

    public async Task<RefreshResult> RefreshAsync(string rawRefreshToken, CancellationToken cancellationToken)
    {
        var rotation = await _refreshTokenService.RotateAsync(rawRefreshToken, SubjectType.PlatformAdmin, expectedTenantId: null, cancellationToken);
        if (rotation.Outcome != TokenRotationOutcome.Success)
        {
            return RefreshResult.Failure(MapRefreshOutcome(rotation.Outcome));
        }

        var admin = await _repository.GetByIdAsync(rotation.SubjectId!.Value, cancellationToken);
        var now = _dateTimeProvider.UtcNow;

        if (admin is null || !admin.CanAuthenticate(now))
        {
            // Re-checked at refresh time per docs/architecture/authorization.md —
            // a stored refresh token must not keep working after the account
            // becomes unavailable.
            await _refreshTokenService.RevokeFamilyAsync(rotation.Token!.FamilyId, "account_unavailable", cancellationToken);
            return RefreshResult.Failure(RefreshOutcome.AccountUnavailable);
        }

        var accessToken = _accessTokenGenerator.GenerateForPlatformAdmin(admin);
        return RefreshResult.Success(ToSession(accessToken, rotation.Token!));
    }

    public Task LogoutAsync(string rawRefreshToken, CancellationToken cancellationToken) =>
        _refreshTokenService.RevokeByRawTokenAsync(rawRefreshToken, "logout", cancellationToken);

    public async Task LogoutAllAsync(Guid adminId, CancellationToken cancellationToken)
    {
        await _refreshTokenService.RevokeAllForSubjectAsync(SubjectType.PlatformAdmin, adminId, "logout_all", cancellationToken);

        await _auditLogger.RecordAsync(new AuditEntry
        {
            ActorUserId = adminId,
            ActorType = AuditActorType.PlatformAdmin,
            Action = "LogoutAll",
            EntityType = nameof(PlatformAdmin),
            EntityId = adminId.ToString()
        }, cancellationToken);
    }

    public async Task<ChangePasswordOutcome> ChangePasswordAsync(Guid adminId, string currentPassword, string newPassword, CancellationToken cancellationToken)
    {
        var admin = await _repository.GetByIdAsync(adminId, cancellationToken);
        var now = _dateTimeProvider.UtcNow;

        if (admin is null || !admin.CanAuthenticate(now))
        {
            return ChangePasswordOutcome.AccountUnavailable;
        }

        if (!_passwordHasher.Verify(currentPassword, admin.PasswordHash))
        {
            return ChangePasswordOutcome.InvalidCurrentPassword;
        }

        if (!IsPasswordPolicyCompliant(newPassword))
        {
            return ChangePasswordOutcome.PasswordPolicyViolation;
        }

        admin.ChangePasswordHash(_passwordHasher.Hash(newPassword), now);
        await _repository.SaveChangesAsync(cancellationToken);

        await _refreshTokenService.RevokeAllForSubjectAsync(SubjectType.PlatformAdmin, adminId, "password_changed", cancellationToken);

        await _auditLogger.RecordAsync(new AuditEntry
        {
            ActorUserId = adminId,
            ActorType = AuditActorType.PlatformAdmin,
            Action = "PasswordChanged",
            EntityType = nameof(PlatformAdmin),
            EntityId = adminId.ToString()
        }, cancellationToken);

        return ChangePasswordOutcome.Success;
    }

    private bool IsPasswordPolicyCompliant(string password) =>
        password.Length >= _passwordPolicy.MinLength && password.Length <= _passwordPolicy.MaxLength;

    private static string NormalizeForLookup(string email) => email.Trim().ToUpperInvariant();

    private static IssuedSession ToSession(AccessTokenResult accessToken, IssuedRefreshToken refreshToken) => new()
    {
        AccessToken = accessToken.Token,
        AccessTokenExpiresAt = accessToken.ExpiresAt,
        RawRefreshToken = refreshToken.RawToken,
        RefreshTokenExpiresAt = refreshToken.ExpiresAt
    };

    private static RefreshOutcome MapRefreshOutcome(TokenRotationOutcome outcome) => outcome switch
    {
        TokenRotationOutcome.Reused => RefreshOutcome.Reused,
        TokenRotationOutcome.Expired => RefreshOutcome.Expired,
        TokenRotationOutcome.ContextMismatch => RefreshOutcome.ContextMismatch,
        TokenRotationOutcome.Revoked or TokenRotationOutcome.NotFound => RefreshOutcome.Invalid,
        _ => RefreshOutcome.Invalid
    };
}
