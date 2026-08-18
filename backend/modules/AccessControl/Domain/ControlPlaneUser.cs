using Knight.Domain.Common;
using Knight.Domain.Exceptions;

namespace AccessControl.Domain;

/// <summary>
/// A human being who signs in to the KNIGHT dashboard. One account belongs to at
/// most one customer: <see cref="CustomerId"/> null means platform staff, and a
/// value means the account can only ever see that customer's world
/// (docs/domain-model.md section 1).
///
/// The account owns its own lockout and MFA state so authentication services do
/// not each reimplement the rules. Nothing here knows about tokens or HTTP.
/// </summary>
public sealed class ControlPlaneUser : AuditableEntity, ICustomerScoped
{
    public Guid? CustomerId { get; private set; }

    public string Email { get; private set; }

    /// <summary>Uppercase-invariant form of <see cref="Email"/>; the only column lookup and uniqueness use.</summary>
    public string NormalizedEmail { get; private set; }

    public string DisplayName { get; private set; }

    public string PasswordHash { get; private set; }

    public AccountStatus Status { get; private set; }

    /// <summary>True once a TOTP secret has been enrolled and confirmed with a valid code.</summary>
    public bool MfaEnabled { get; private set; }

    /// <summary>The shared TOTP secret. Set during enrolment, cleared when MFA is disabled.</summary>
    public string? MfaSecret { get; private set; }

    public DateTimeOffset? MfaConfirmedAt { get; private set; }

    public int FailedLoginCount { get; private set; }

    public DateTimeOffset? LockedUntil { get; private set; }

    public DateTimeOffset? LastLoginAt { get; private set; }

    private readonly List<UserRoleAssignment> _roles = [];

    public IReadOnlyCollection<UserRoleAssignment> Roles => _roles.AsReadOnly();

    public bool IsPlatformStaff => CustomerId is null;

    private ControlPlaneUser()
    {
        Email = string.Empty;
        NormalizedEmail = string.Empty;
        DisplayName = string.Empty;
        PasswordHash = string.Empty;
    }

    private ControlPlaneUser(
        Guid id,
        DateTimeOffset createdAt,
        Guid? customerId,
        string email,
        string normalizedEmail,
        string displayName,
        string passwordHash)
        : base(id, createdAt)
    {
        CustomerId = customerId;
        Email = email;
        NormalizedEmail = normalizedEmail;
        DisplayName = displayName;
        PasswordHash = passwordHash;
        Status = AccountStatus.Invited;
    }

    /// <summary>Creates a platform staff account, which is not bound to any customer.</summary>
    public static ControlPlaneUser CreatePlatformStaff(
        Guid id,
        DateTimeOffset createdAt,
        string email,
        string displayName,
        string passwordHash)
        => Create(id, createdAt, null, email, displayName, passwordHash);

    /// <summary>Creates an account that can only ever see one customer.</summary>
    public static ControlPlaneUser CreateCustomerUser(
        Guid id,
        DateTimeOffset createdAt,
        Guid customerId,
        string email,
        string displayName,
        string passwordHash)
    {
        if (customerId == Guid.Empty)
        {
            throw DomainException.Validation("A customer user must belong to a customer.");
        }

        return Create(id, createdAt, customerId, email, displayName, passwordHash);
    }

    private static ControlPlaneUser Create(
        Guid id,
        DateTimeOffset createdAt,
        Guid? customerId,
        string email,
        string displayName,
        string passwordHash)
    {
        if (string.IsNullOrWhiteSpace(passwordHash))
        {
            throw DomainException.Validation("Password hash is required.");
        }

        if (string.IsNullOrWhiteSpace(displayName))
        {
            throw DomainException.Validation("Display name is required.");
        }

        return new ControlPlaneUser(
            id,
            createdAt,
            customerId,
            EmailAddress.Normalize(email),
            EmailAddress.NormalizeForComparison(email),
            displayName.Trim(),
            passwordHash);
    }

    // --- Lifecycle -------------------------------------------------------

    public void Activate(DateTimeOffset now)
    {
        if (Status is AccountStatus.Disabled)
        {
            throw DomainException.Conflict("A disabled account cannot be activated.");
        }

        Status = AccountStatus.Active;
        MarkUpdated(now);
    }

    public void Suspend(DateTimeOffset now)
    {
        Status = AccountStatus.Suspended;
        MarkUpdated(now);
    }

    public void Disable(DateTimeOffset now)
    {
        Status = AccountStatus.Disabled;
        MarkUpdated(now);
    }

    public void Rename(string displayName, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(displayName))
        {
            throw DomainException.Validation("Display name is required.");
        }

        DisplayName = displayName.Trim();
        MarkUpdated(now);
    }

    public void ChangePasswordHash(string passwordHash, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(passwordHash))
        {
            throw DomainException.Validation("Password hash is required.");
        }

        PasswordHash = passwordHash;
        MarkUpdated(now);
    }

    // --- Authentication state -------------------------------------------

    public bool CanAuthenticate(DateTimeOffset now) => Status is AccountStatus.Active && !IsLocked(now);

    public bool IsLocked(DateTimeOffset now) => LockedUntil is not null && LockedUntil > now;

    /// <summary>
    /// Counts a failed credential or MFA attempt and locks the account once the
    /// threshold is reached. Lockout is deliberate defence in depth alongside the
    /// per-IP rate limit: one throttles a network source, the other protects a
    /// specific account (docs/authentication.md section 1).
    /// </summary>
    public void RegisterFailedLogin(DateTimeOffset now, int lockoutThreshold, TimeSpan lockoutDuration)
    {
        if (lockoutThreshold < 1)
        {
            throw DomainException.Validation("The lockout threshold must be at least one attempt.");
        }

        FailedLoginCount++;

        if (FailedLoginCount >= lockoutThreshold)
        {
            LockedUntil = now.Add(lockoutDuration);
        }

        MarkUpdated(now);
    }

    public void RegisterSuccessfulLogin(DateTimeOffset now)
    {
        FailedLoginCount = 0;
        LockedUntil = null;
        LastLoginAt = now;
        MarkUpdated(now);
    }

    public void Unlock(DateTimeOffset now)
    {
        FailedLoginCount = 0;
        LockedUntil = null;
        MarkUpdated(now);
    }

    // --- Multi-factor authentication ------------------------------------

    /// <summary>
    /// Stores a freshly generated TOTP secret. MFA is not yet in force: the
    /// account must prove it can produce a valid code first, so a mistyped or
    /// mis-scanned secret cannot lock the owner out of their own account.
    /// </summary>
    public void BeginMfaEnrollment(string secret, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(secret))
        {
            throw DomainException.Validation("An MFA secret is required.");
        }

        if (MfaEnabled)
        {
            throw DomainException.Conflict("MFA is already enabled for this account.");
        }

        MfaSecret = secret;
        MfaConfirmedAt = null;
        MarkUpdated(now);
    }

    /// <summary>Confirms enrolment once the owner has produced a valid code.</summary>
    public void ConfirmMfa(DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(MfaSecret))
        {
            throw DomainException.Conflict("MFA enrolment has not been started.");
        }

        MfaEnabled = true;
        MfaConfirmedAt = now;
        MarkUpdated(now);
    }

    public void DisableMfa(DateTimeOffset now)
    {
        MfaEnabled = false;
        MfaSecret = null;
        MfaConfirmedAt = null;
        MarkUpdated(now);
    }

    // --- Role assignment -------------------------------------------------

    /// <summary>
    /// Grants a role. The scopes must agree: a customer account holding a
    /// platform role would escape its own isolation boundary, and a platform
    /// account holding a customer role would be assigned to a customer it does
    /// not belong to.
    /// </summary>
    public UserRoleAssignment AssignRole(Guid assignmentId, Role role, DateTimeOffset now)
    {
        if (role.Scope is RoleScope.Platform && !IsPlatformStaff)
        {
            throw DomainException.Conflict("A customer account cannot hold a platform-scoped role.");
        }

        if (role.Scope is RoleScope.Customer && IsPlatformStaff)
        {
            throw DomainException.Conflict("A platform account cannot hold a customer-scoped role.");
        }

        if (_roles.Any(r => r.RoleId == role.Id))
        {
            throw DomainException.Conflict("The account already holds this role.");
        }

        var assignment = UserRoleAssignment.Create(assignmentId, Id, role.Id, CustomerId, now);
        _roles.Add(assignment);
        MarkUpdated(now);
        return assignment;
    }

    public void RemoveRole(Guid roleId, DateTimeOffset now)
    {
        var assignment = _roles.SingleOrDefault(r => r.RoleId == roleId)
            ?? throw DomainException.Conflict("The account does not hold this role.");

        _roles.Remove(assignment);
        MarkUpdated(now);
    }
}
