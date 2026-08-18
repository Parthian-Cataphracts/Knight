using AccessControl.Domain;

namespace AccessControl;

/// <summary>Why a login attempt did not produce a session.</summary>
public enum AuthenticationOutcome
{
    Succeeded = 0,

    /// <summary>Wrong email, wrong password, or an account that may not authenticate. Deliberately indistinguishable to the caller.</summary>
    InvalidCredentials = 1,

    /// <summary>The account has MFA enabled and no valid code was supplied.</summary>
    MfaRequired = 2,

    /// <summary>The account holds a role that requires a second factor but has not enrolled one yet.</summary>
    MfaEnrollmentRequired = 3,
}

public sealed record AuthenticatedPrincipal(
    Guid UserId,
    Guid? CustomerId,
    string Email,
    string DisplayName,
    IReadOnlyCollection<string> Roles,
    IReadOnlyCollection<string> Permissions,
    bool MfaEnabled,
    bool MfaSatisfied);

public sealed record AuthenticationResult(
    AuthenticationOutcome Outcome,
    string? AccessToken,
    DateTimeOffset? AccessTokenExpiresAt,
    string? RefreshToken,
    DateTimeOffset? RefreshTokenExpiresAt,
    AuthenticatedPrincipal? Principal)
{
    public static AuthenticationResult Failed(AuthenticationOutcome outcome) =>
        new(outcome, null, null, null, null, null);
}

public sealed record LoginRequest(string Email, string Password, string? MfaCode, string? IpAddress, string? UserAgent);

public sealed record MfaEnrollment(string Secret, string EnrollmentUri);

/// <summary>
/// Authentication for dashboard users. Store and agent principals authenticate
/// through entirely different paths and never reach this service
/// (docs/authentication.md section 4).
/// </summary>
public interface IControlPlaneAuthenticationService
{
    Task<AuthenticationResult> LoginAsync(LoginRequest request, CancellationToken cancellationToken);

    /// <summary>Exchanges a refresh token for a new pair, revoking the whole family if the token was already used.</summary>
    Task<AuthenticationResult> RefreshAsync(string refreshToken, string? ipAddress, string? userAgent, CancellationToken cancellationToken);

    Task LogoutAsync(string refreshToken, CancellationToken cancellationToken);

    Task<AuthenticatedPrincipal?> DescribeAsync(Guid userId, Guid? sessionId, CancellationToken cancellationToken);

    Task<MfaEnrollment> BeginMfaEnrollmentAsync(Guid userId, CancellationToken cancellationToken);

    /// <summary>Confirms enrolment and marks the current session as having satisfied the second factor.</summary>
    Task<AuthenticationResult> ConfirmMfaAsync(Guid userId, Guid sessionId, string code, CancellationToken cancellationToken);
}
