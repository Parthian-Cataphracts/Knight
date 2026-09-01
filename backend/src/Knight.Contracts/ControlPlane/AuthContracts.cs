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

// --- Public self-service registration -----------------------------------------

/// <summary>
/// Public sign-up (docs/self-service-saas-plan.md §11.1). The response is the
/// same whether or not the email was already taken, so this never confirms an
/// account exists.
/// </summary>
public sealed record RegisterRequest
{
    public required string Email { get; init; }

    public required string Password { get; init; }

    /// <summary>The person's name; becomes the account's display name.</summary>
    public required string Name { get; init; }

    /// <summary>The business name, if given; otherwise the customer is named after the person.</summary>
    public string? CompanyName { get; init; }
}

public sealed record VerifyEmailRequest
{
    public required string Token { get; init; }
}

public sealed record ResendVerificationRequest
{
    public required string Email { get; init; }
}

/// <summary>
/// The deliberately generic answer to registration and resend. It says what
/// happens next without ever saying whether an account already existed.
/// </summary>
public sealed record RegistrationAcceptedResponse
{
    public required string Status { get; init; }
}

// --- Account administration ---------------------------------------------------

public sealed record CreateAccountRequest
{
    public required string Email { get; init; }

    public required string DisplayName { get; init; }

    /// <summary>The customer this account belongs to, or null for platform staff. Platform staff only.</summary>
    public Guid? CustomerId { get; init; }

    public IReadOnlyCollection<Guid> RoleIds { get; init; } = [];
}

/// <summary>
/// Exactly one of the two ways a new account is handed over.
///
/// Where mail can leave this deployment, the account's holder gets an activation
/// link and nobody — including the administrator who created the account — ever
/// learns a password. Where it cannot, a one-time password is returned here,
/// once, and there is no endpoint that reads it back — an administrator who
/// loses it resets the account rather than looking it up, which is what stops
/// "can reset an account" from also meaning "can silently become that account".
/// </summary>
public sealed record CreatedAccountResponse
{
    public required AccountResponse Account { get; init; }

    /// <summary>Null when an invitation was emailed instead.</summary>
    public string? TemporaryPassword { get; init; }

    public required bool InvitationSent { get; init; }
}

/// <summary>Completes an invitation: the holder of the emailed token chooses the account's password.</summary>
public sealed record ActivateAccountRequest
{
    public required string Token { get; init; }

    public required string Password { get; init; }
}

public sealed record RenameAccountRequest
{
    public required string DisplayName { get; init; }
}

public sealed record SetAccountRolesRequest
{
    /// <summary>The roles the account should hold afterwards. Replaces, rather than adds to, what it holds now.</summary>
    public required IReadOnlyCollection<Guid> RoleIds { get; init; }
}

public sealed record TemporaryPasswordResponse
{
    /// <summary>Null when a fresh invitation was emailed instead of a password being generated.</summary>
    public string? TemporaryPassword { get; init; }

    public required bool InvitationSent { get; init; }
}

public sealed record CreateRoleRequest
{
    public required string Name { get; init; }

    public string? Description { get; init; }

    /// <summary>Platform or Customer.</summary>
    public required string Scope { get; init; }

    public Guid? CustomerId { get; init; }

    public IReadOnlyCollection<string> Permissions { get; init; } = [];
}

public sealed record SetRolePermissionsRequest
{
    public required IReadOnlyCollection<string> Permissions { get; init; }
}
