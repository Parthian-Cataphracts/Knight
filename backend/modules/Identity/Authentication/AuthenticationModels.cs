namespace Identity.Authentication;

/// <summary>
/// Internal outcome of a login attempt. Endpoints must collapse every non-success
/// value to one generic external failure response — see
/// docs/architecture/authorization.md ("login enumeration resistance"). The
/// distinction exists only for internal logging/auditing.
/// </summary>
public enum LoginOutcome
{
    Success,
    InvalidCredentials,
    AccountLocked,
    AccountUnavailable
}

public sealed record IssuedSession
{
    public required string AccessToken { get; init; }
    public required DateTimeOffset AccessTokenExpiresAt { get; init; }
    public required string RawRefreshToken { get; init; }
    public required DateTimeOffset RefreshTokenExpiresAt { get; init; }
}

public sealed record LoginResult
{
    public required LoginOutcome Outcome { get; init; }
    public IssuedSession? Session { get; init; }
    public Guid? SubjectId { get; init; }

    public static LoginResult Success(IssuedSession session, Guid subjectId) =>
        new() { Outcome = LoginOutcome.Success, Session = session, SubjectId = subjectId };

    public static LoginResult Failure(LoginOutcome outcome) => new() { Outcome = outcome };
}

/// <summary>
/// Internal outcome of a refresh attempt. Endpoints must collapse every
/// non-success value to one generic external failure response.
/// </summary>
public enum RefreshOutcome
{
    Success,
    Invalid,
    Reused,
    Expired,
    ContextMismatch,
    AccountUnavailable
}

public sealed record RefreshResult
{
    public required RefreshOutcome Outcome { get; init; }
    public IssuedSession? Session { get; init; }

    public static RefreshResult Success(IssuedSession session) => new() { Outcome = RefreshOutcome.Success, Session = session };

    public static RefreshResult Failure(RefreshOutcome outcome) => new() { Outcome = outcome };
}

public enum ChangePasswordOutcome
{
    Success,
    InvalidCurrentPassword,
    PasswordPolicyViolation,
    AccountUnavailable
}
