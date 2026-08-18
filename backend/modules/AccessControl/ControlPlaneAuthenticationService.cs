using AccessControl.Abstractions;
using AccessControl.Domain;
using Knight.Application.Abstractions.ControlPlane;
using Knight.Application.Abstractions.Time;
using Microsoft.Extensions.Options;

namespace AccessControl;

/// <summary>
/// Password, second factor and refresh-token handling for dashboard users.
///
/// Two properties matter more than the mechanics: a caller can never tell an
/// unknown account from a wrong password from a locked one, and a refresh token
/// that has already been exchanged revokes its entire family rather than merely
/// failing — presenting a consumed token is what a stolen token looks like
/// (docs/authentication.md section 1).
/// </summary>
internal sealed class ControlPlaneAuthenticationService : IControlPlaneAuthenticationService
{
    private readonly IControlPlaneUserRepository _users;
    private readonly IUserSessionRepository _sessions;
    private readonly IEffectivePermissionResolver _permissions;
    private readonly IControlPlanePasswordHasher _passwordHasher;
    private readonly ISecureTokenFactory _tokenFactory;
    private readonly IControlPlaneTokenGenerator _accessTokens;
    private readonly ITotpService _totp;
    private readonly IAuditTrail _audit;
    private readonly IDateTimeProvider _clock;
    private readonly AccessControlOptions _options;

    public ControlPlaneAuthenticationService(
        IControlPlaneUserRepository users,
        IUserSessionRepository sessions,
        IEffectivePermissionResolver permissions,
        IControlPlanePasswordHasher passwordHasher,
        ISecureTokenFactory tokenFactory,
        IControlPlaneTokenGenerator accessTokens,
        ITotpService totp,
        IAuditTrail audit,
        IDateTimeProvider clock,
        IOptions<AccessControlOptions> options)
    {
        _users = users;
        _sessions = sessions;
        _permissions = permissions;
        _passwordHasher = passwordHasher;
        _tokenFactory = tokenFactory;
        _accessTokens = accessTokens;
        _totp = totp;
        _audit = audit;
        _clock = clock;
        _options = options.Value;
    }

    public async Task<AuthenticationResult> LoginAsync(LoginRequest request, CancellationToken cancellationToken)
    {
        var now = _clock.UtcNow;

        ControlPlaneUser? user;
        try
        {
            user = await _users.FindForAuthenticationAsync(EmailAddress.NormalizeForComparison(request.Email), cancellationToken);
        }
        catch (Knight.Domain.Exceptions.DomainException)
        {
            // A malformed address is simply not a known account; it must not be
            // distinguishable from a wrong password.
            return AuthenticationResult.Failed(AuthenticationOutcome.InvalidCredentials);
        }

        if (user is null)
        {
            // Hash anyway so a missing account does not answer measurably faster
            // than a wrong password for an existing one.
            _passwordHasher.Verify(request.Password, DummyHash);
            return AuthenticationResult.Failed(AuthenticationOutcome.InvalidCredentials);
        }

        if (!user.CanAuthenticate(now))
        {
            await AuditAsync(user, "auth.login.rejected", user.IsLocked(now) ? "locked" : user.Status.ToString(), cancellationToken);
            return AuthenticationResult.Failed(AuthenticationOutcome.InvalidCredentials);
        }

        if (!_passwordHasher.Verify(request.Password, user.PasswordHash))
        {
            user.RegisterFailedLogin(now, _options.LockoutThreshold, _options.LockoutDuration);
            await _users.SaveChangesAsync(cancellationToken);
            await AuditAsync(user, user.IsLocked(now) ? "auth.login.lockout" : "auth.login.failed", "invalid_password", cancellationToken);
            return AuthenticationResult.Failed(AuthenticationOutcome.InvalidCredentials);
        }

        var roles = await _permissions.GetRoleNamesAsync(user.Id, cancellationToken);
        var mfaExpected = user.MfaEnabled || SystemRoles.RequiresMfa(roles);

        if (mfaExpected && !user.MfaEnabled)
        {
            // The account must set a second factor up before it can do anything.
            // It still gets a session, but one flagged as not having satisfied
            // MFA — the authorization layer lets it reach nothing but enrolment.
            user.RegisterSuccessfulLogin(now);
            await _users.SaveChangesAsync(cancellationToken);
            var pending = await IssueSessionAsync(user, roles, mfaSatisfied: false, request.IpAddress, request.UserAgent, now, cancellationToken);
            await AuditAsync(user, "auth.login.mfa_enrollment_required", null, cancellationToken);
            return pending with { Outcome = AuthenticationOutcome.MfaEnrollmentRequired };
        }

        if (user.MfaEnabled)
        {
            if (string.IsNullOrWhiteSpace(request.MfaCode))
            {
                return AuthenticationResult.Failed(AuthenticationOutcome.MfaRequired);
            }

            if (!_totp.Verify(user.MfaSecret!, request.MfaCode, now))
            {
                // A wrong code counts against the same lockout budget as a wrong
                // password; otherwise the second factor is brute-forceable.
                user.RegisterFailedLogin(now, _options.LockoutThreshold, _options.LockoutDuration);
                await _users.SaveChangesAsync(cancellationToken);
                await AuditAsync(user, "auth.login.mfa_failed", null, cancellationToken);
                return AuthenticationResult.Failed(AuthenticationOutcome.InvalidCredentials);
            }
        }

        user.RegisterSuccessfulLogin(now);
        await _users.SaveChangesAsync(cancellationToken);

        var result = await IssueSessionAsync(user, roles, mfaSatisfied: true, request.IpAddress, request.UserAgent, now, cancellationToken);
        await AuditAsync(user, "auth.login.succeeded", null, cancellationToken);
        return result;
    }

    public async Task<AuthenticationResult> RefreshAsync(string refreshToken, string? ipAddress, string? userAgent, CancellationToken cancellationToken)
    {
        var now = _clock.UtcNow;

        if (string.IsNullOrWhiteSpace(refreshToken))
        {
            return AuthenticationResult.Failed(AuthenticationOutcome.InvalidCredentials);
        }

        var session = await _sessions.FindByTokenHashAsync(_tokenFactory.Hash(refreshToken), cancellationToken);
        if (session is null)
        {
            return AuthenticationResult.Failed(AuthenticationOutcome.InvalidCredentials);
        }

        if (session.IsReplay(now))
        {
            await _sessions.RevokeFamilyAsync(session.FamilyId, now, "refresh_token_reuse", cancellationToken);
            await _sessions.SaveChangesAsync(cancellationToken);
            await _audit.RecordAsync(
                "auth.refresh.reuse_detected",
                nameof(UserSession),
                session.Id.ToString(),
                session.CustomerId,
                cancellationToken);
            return AuthenticationResult.Failed(AuthenticationOutcome.InvalidCredentials);
        }

        if (!session.IsActive(now))
        {
            return AuthenticationResult.Failed(AuthenticationOutcome.InvalidCredentials);
        }

        // Unfiltered: a refresh request has not established a customer scope yet,
        // so the filtered read would hide the account from its own refresh.
        var user = await _users.FindForAuthenticationByIdAsync(session.UserId, cancellationToken);
        if (user is null || !user.CanAuthenticate(now))
        {
            await _sessions.RevokeFamilyAsync(session.FamilyId, now, "account_not_authenticable", cancellationToken);
            await _sessions.SaveChangesAsync(cancellationToken);
            return AuthenticationResult.Failed(AuthenticationOutcome.InvalidCredentials);
        }

        var generated = _tokenFactory.Generate();
        var replacement = session.Rotate(Guid.NewGuid(), generated.Hash, now);
        await _sessions.AddAsync(replacement, cancellationToken);
        await _sessions.SaveChangesAsync(cancellationToken);

        var roles = await _permissions.GetRoleNamesAsync(user.Id, cancellationToken);
        var permissions = await _permissions.ResolveAsync(user.Id, cancellationToken);
        var accessToken = _accessTokens.Issue(user, replacement, roles);

        return new AuthenticationResult(
            AuthenticationOutcome.Succeeded,
            accessToken.Token,
            accessToken.ExpiresAt,
            generated.RawValue,
            replacement.ExpiresAt,
            Describe(user, roles, permissions, replacement.MfaSatisfied));
    }

    public async Task LogoutAsync(string refreshToken, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(refreshToken))
        {
            return;
        }

        var session = await _sessions.FindByTokenHashAsync(_tokenFactory.Hash(refreshToken), cancellationToken);
        if (session is null)
        {
            return;
        }

        // The whole family goes, not just the presented token: logging out has to
        // end the login, not merely the current leg of its rotation.
        await _sessions.RevokeFamilyAsync(session.FamilyId, _clock.UtcNow, "logout", cancellationToken);
        await _sessions.SaveChangesAsync(cancellationToken);
        await _audit.RecordAsync("auth.logout", nameof(UserSession), session.Id.ToString(), session.CustomerId, cancellationToken);
    }

    public async Task<AuthenticatedPrincipal?> DescribeAsync(Guid userId, Guid? sessionId, CancellationToken cancellationToken)
    {
        var user = await _users.GetByIdAsync(userId, cancellationToken);
        if (user is null)
        {
            return null;
        }

        var mfaSatisfied = true;
        if (sessionId is not null)
        {
            var session = await _sessions.GetByIdAsync(sessionId.Value, cancellationToken);
            mfaSatisfied = session?.MfaSatisfied ?? false;
        }

        var roles = await _permissions.GetRoleNamesAsync(user.Id, cancellationToken);
        var permissions = await _permissions.ResolveAsync(user.Id, cancellationToken);
        return Describe(user, roles, permissions, mfaSatisfied);
    }

    public async Task<MfaEnrollment> BeginMfaEnrollmentAsync(Guid userId, CancellationToken cancellationToken)
    {
        var user = await _users.GetByIdAsync(userId, cancellationToken)
            ?? throw new Knight.Application.Exceptions.NotFoundException("The account was not found.");

        var secret = _totp.GenerateSecret();
        user.BeginMfaEnrollment(secret, _clock.UtcNow);
        await _users.SaveChangesAsync(cancellationToken);

        // The secret itself is never audited — only the fact that enrolment began.
        await AuditAsync(user, "auth.mfa.enrollment_started", null, cancellationToken);

        return new MfaEnrollment(secret, _totp.BuildEnrollmentUri(secret, user.Email, _options.MfaIssuer));
    }

    public async Task<AuthenticationResult> ConfirmMfaAsync(Guid userId, Guid sessionId, string code, CancellationToken cancellationToken)
    {
        var now = _clock.UtcNow;

        var user = await _users.GetByIdAsync(userId, cancellationToken)
            ?? throw new Knight.Application.Exceptions.NotFoundException("The account was not found.");

        if (string.IsNullOrWhiteSpace(user.MfaSecret) || !_totp.Verify(user.MfaSecret, code, now))
        {
            await AuditAsync(user, "auth.mfa.confirmation_failed", null, cancellationToken);
            return AuthenticationResult.Failed(AuthenticationOutcome.InvalidCredentials);
        }

        user.ConfirmMfa(now);
        await _users.SaveChangesAsync(cancellationToken);

        var session = await _sessions.GetByIdAsync(sessionId, cancellationToken);
        if (session is null || !session.IsActive(now) || session.UserId != user.Id)
        {
            return AuthenticationResult.Failed(AuthenticationOutcome.InvalidCredentials);
        }

        session.MarkMfaSatisfied();
        await _sessions.SaveChangesAsync(cancellationToken);

        var roles = await _permissions.GetRoleNamesAsync(user.Id, cancellationToken);
        var permissions = await _permissions.ResolveAsync(user.Id, cancellationToken);
        var accessToken = _accessTokens.Issue(user, session, roles);
        await AuditAsync(user, "auth.mfa.enabled", null, cancellationToken);

        return new AuthenticationResult(
            AuthenticationOutcome.Succeeded,
            accessToken.Token,
            accessToken.ExpiresAt,
            null,
            null,
            Describe(user, roles, permissions, mfaSatisfied: true));
    }

    private async Task<AuthenticationResult> IssueSessionAsync(
        ControlPlaneUser user,
        IReadOnlyCollection<string> roles,
        bool mfaSatisfied,
        string? ipAddress,
        string? userAgent,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var generated = _tokenFactory.Generate();
        var session = UserSession.StartFamily(
            Guid.NewGuid(),
            user,
            generated.Hash,
            now,
            _options.SessionLifetime,
            mfaSatisfied,
            ipAddress,
            userAgent);

        await _sessions.AddAsync(session, cancellationToken);
        await _sessions.SaveChangesAsync(cancellationToken);

        var permissions = await _permissions.ResolveAsync(user.Id, cancellationToken);
        var accessToken = _accessTokens.Issue(user, session, roles);

        return new AuthenticationResult(
            AuthenticationOutcome.Succeeded,
            accessToken.Token,
            accessToken.ExpiresAt,
            generated.RawValue,
            session.ExpiresAt,
            Describe(user, roles, permissions, mfaSatisfied));
    }

    private static AuthenticatedPrincipal Describe(
        ControlPlaneUser user,
        IReadOnlyCollection<string> roles,
        IReadOnlyCollection<string> permissions,
        bool mfaSatisfied)
        => new(
            user.Id,
            user.CustomerId,
            user.Email,
            user.DisplayName,
            roles,
            permissions,
            user.MfaEnabled,
            mfaSatisfied);

    private Task AuditAsync(ControlPlaneUser user, string action, string? reason, CancellationToken cancellationToken) =>
        _audit.RecordAsync(
            action,
            nameof(ControlPlaneUser),
            user.Id.ToString(),
            user.CustomerId,
            cancellationToken,
            newValue: reason is null ? null : new { reason });

    /// <summary>
    /// A structurally valid hash of a value nobody knows, used only to keep the
    /// unknown-account path as expensive as the known-account one.
    /// </summary>
    private const string DummyHash = "210000.AAAAAAAAAAAAAAAAAAAAAA==.AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=";
}
