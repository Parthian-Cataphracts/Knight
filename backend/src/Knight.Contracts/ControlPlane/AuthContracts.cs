namespace Knight.Contracts.ControlPlane;

/// <summary>
/// Dashboard authentication payloads (docs/api-contracts.md section 2).
/// The refresh token also travels as an HttpOnly cookie; the body field exists
/// for non-browser clients and for tests.
/// </summary>
public sealed record LoginRequest
{
    public required string Email { get; init; }

    public required string Password { get; init; }

    /// <summary>Six-digit TOTP code, supplied on the second leg of a login that requires MFA.</summary>
    public string? MfaCode { get; init; }
}

public sealed record RefreshRequest
{
    public string? RefreshToken { get; init; }
}

public sealed record MfaCodeRequest
{
    public required string Code { get; init; }
}

public sealed record AuthenticatedUserResponse
{
    public required Guid Id { get; init; }

    public required string Email { get; init; }

    public required string DisplayName { get; init; }

    public Guid? CustomerId { get; init; }

    public required IReadOnlyCollection<string> Roles { get; init; }

    public required IReadOnlyCollection<string> Permissions { get; init; }

    public required bool MfaEnabled { get; init; }

    /// <summary>False while a required second factor is still outstanding for this session.</summary>
    public required bool MfaSatisfied { get; init; }
}

public sealed record LoginResponse
{
    /// <summary>"succeeded", "mfa_required" or "mfa_enrollment_required".</summary>
    public required string Status { get; init; }

    public string? AccessToken { get; init; }

    public DateTimeOffset? ExpiresAt { get; init; }

    /// <summary>Returned for non-browser clients; browsers get it as an HttpOnly cookie instead.</summary>
    public string? RefreshToken { get; init; }

    public AuthenticatedUserResponse? User { get; init; }
}

public sealed record MfaEnrollmentResponse
{
    /// <summary>The shared secret, shown once so it can be typed in manually.</summary>
    public required string Secret { get; init; }

    public required string EnrollmentUri { get; init; }
}
