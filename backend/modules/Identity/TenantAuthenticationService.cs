using Identity.Abstractions;
using Identity.Authentication;
using Identity.Domain;
using Identity.Options;
using Microsoft.Extensions.Options;
using Knight.Application.Abstractions.Auditing;
using Knight.Application.Abstractions.Time;

namespace Identity;

public sealed class TenantAuthenticationService : ITenantAuthenticationService
{
    private readonly ITenantUserRepository _repository;
    private readonly IPasswordHasher _passwordHasher;
    private readonly IAccessTokenGenerator _accessTokenGenerator;
    private readonly IRefreshTokenService _refreshTokenService;
    private readonly IEffectivePermissionService _effectivePermissionService;
    private readonly IDateTimeProvider _dateTimeProvider;
    private readonly IAuditLogger _auditLogger;
    private readonly PasswordPolicyOptions _passwordPolicy;
    private readonly CredentialVerifier _credentialVerifier;

    public TenantAuthenticationService(
        ITenantUserRepository repository,
        IPasswordHasher passwordHasher,
        IAccessTokenGenerator accessTokenGenerator,
        IRefreshTokenService refreshTokenService,
        IEffectivePermissionService effectivePermissionService,
        IDateTimeProvider dateTimeProvider,
        IAuditLogger auditLogger,
        IOptions<LockoutOptions> lockoutOptions,
        IOptions<PasswordPolicyOptions> passwordPolicyOptions)
    {
        _repository = repository;
        _passwordHasher = passwordHasher;
        _accessTokenGenerator = accessTokenGenerator;
        _refreshTokenService = refreshTokenService;
        _effectivePermissionService = effectivePermissionService;
        _dateTimeProvider = dateTimeProvider;
        _auditLogger = auditLogger;
        _passwordPolicy = passwordPolicyOptions.Value;
        _credentialVerifier = new CredentialVerifier(passwordHasher, lockoutOptions.Value);
    }

    public async Task<LoginResult> LoginAsync(Guid tenantId, string email, string password, CancellationToken cancellationToken)
    {
        var now = _dateTimeProvider.UtcNow;
        var normalizedEmail = NormalizeForLookup(email);
        var user = await _repository.GetByNormalizedEmailAsync(tenantId, normalizedEmail, cancellationToken);

        var outcome = _credentialVerifier.Verify(user, password, now);

        if (user is not null)
        {
            var rehash = outcome == LoginOutcome.Success ? _credentialVerifier.RehashIfNeeded(user.PasswordHash, password) : null;
            if (rehash is not null)
            {
                user.ChangePasswordHash(rehash, now);
            }

            await _repository.SaveChangesAsync(cancellationToken);
        }

        if (outcome != LoginOutcome.Success || user is null)
        {
            if (outcome == LoginOutcome.AccountLocked && user is not null)
            {
                await _auditLogger.RecordAsync(new AuditEntry
                {
                    ActorUserId = user.Id,
                    ActorType = AuditActorType.TenantUser,
                    TenantId = tenantId,
                    Action = "AccountLocked",
                    EntityType = nameof(TenantUser),
                    EntityId = user.Id.ToString()
                }, cancellationToken);
            }

            return LoginResult.Failure(outcome);
        }

        // Permission claims placed in the access token reflect roles at the
        // moment of login/refresh only; see docs/architecture/authorization.md
        // for why the short access-token lifetime bounds staleness.
        var permissionKeys = await _effectivePermissionService.GetEffectivePermissionKeysAsync(tenantId, user.Id, cancellationToken);
        var accessToken = _accessTokenGenerator.GenerateForTenantUser(user, permissionKeys);
        var refreshToken = await _refreshTokenService.IssueNewFamilyAsync(SubjectType.TenantUser, user.Id, tenantId, cancellationToken);

        await _auditLogger.RecordAsync(new AuditEntry
        {
            ActorUserId = user.Id,
            ActorType = AuditActorType.TenantUser,
            TenantId = tenantId,
            Action = "TenantLoginSucceeded",
            EntityType = nameof(TenantUser),
            EntityId = user.Id.ToString()
        }, cancellationToken);

        return LoginResult.Success(ToSession(accessToken, refreshToken), user.Id);
    }

    public async Task<RefreshResult> RefreshAsync(string rawRefreshToken, Guid expectedTenantId, CancellationToken cancellationToken)
    {
        var rotation = await _refreshTokenService.RotateAsync(rawRefreshToken, SubjectType.TenantUser, expectedTenantId, cancellationToken);
        if (rotation.Outcome != TokenRotationOutcome.Success)
        {
            return RefreshResult.Failure(MapRefreshOutcome(rotation.Outcome));
        }

        var user = await _repository.GetByIdAsync(expectedTenantId, rotation.SubjectId!.Value, cancellationToken);
        var now = _dateTimeProvider.UtcNow;

        if (user is null || !user.CanAuthenticate(now))
        {
            await _refreshTokenService.RevokeFamilyAsync(rotation.Token!.FamilyId, "account_unavailable", cancellationToken);
            return RefreshResult.Failure(RefreshOutcome.AccountUnavailable);
        }

        // Never reuse permission claims from the old access token — always
        // re-resolve current roles/permissions on refresh. See
        // docs/architecture/authorization.md ("refresh permission resolution").
        var permissionKeys = await _effectivePermissionService.GetEffectivePermissionKeysAsync(expectedTenantId, user.Id, cancellationToken);
        var accessToken = _accessTokenGenerator.GenerateForTenantUser(user, permissionKeys);
        return RefreshResult.Success(ToSession(accessToken, rotation.Token!));
    }

    public Task LogoutAsync(string rawRefreshToken, CancellationToken cancellationToken) =>
        _refreshTokenService.RevokeByRawTokenAsync(rawRefreshToken, "logout", cancellationToken);

    public async Task LogoutAllAsync(Guid userId, CancellationToken cancellationToken)
    {
        await _refreshTokenService.RevokeAllForSubjectAsync(SubjectType.TenantUser, userId, "logout_all", cancellationToken);

        await _auditLogger.RecordAsync(new AuditEntry
        {
            ActorUserId = userId,
            ActorType = AuditActorType.TenantUser,
            Action = "LogoutAll",
            EntityType = nameof(TenantUser),
            EntityId = userId.ToString()
        }, cancellationToken);
    }

    public async Task<ChangePasswordOutcome> ChangePasswordAsync(Guid tenantId, Guid userId, string currentPassword, string newPassword, CancellationToken cancellationToken)
    {
        var user = await _repository.GetByIdAsync(tenantId, userId, cancellationToken);
        var now = _dateTimeProvider.UtcNow;

        if (user is null || !user.CanAuthenticate(now))
        {
            return ChangePasswordOutcome.AccountUnavailable;
        }

        if (!_passwordHasher.Verify(currentPassword, user.PasswordHash))
        {
            return ChangePasswordOutcome.InvalidCurrentPassword;
        }

        if (!IsPasswordPolicyCompliant(newPassword))
        {
            return ChangePasswordOutcome.PasswordPolicyViolation;
        }

        user.ChangePasswordHash(_passwordHasher.Hash(newPassword), now);
        await _repository.SaveChangesAsync(cancellationToken);

        await _refreshTokenService.RevokeAllForSubjectAsync(SubjectType.TenantUser, userId, "password_changed", cancellationToken);

        await _auditLogger.RecordAsync(new AuditEntry
        {
            ActorUserId = userId,
            ActorType = AuditActorType.TenantUser,
            TenantId = tenantId,
            Action = "PasswordChanged",
            EntityType = nameof(TenantUser),
            EntityId = userId.ToString()
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
