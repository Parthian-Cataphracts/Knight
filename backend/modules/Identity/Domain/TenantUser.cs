using Knight.Domain.Common;
using Knight.Domain.Exceptions;

namespace Identity.Domain;

/// <summary>
/// A user scoped to a single tenant (owner, administrator, staff, or customer as
/// determined by assigned roles). Never shared across tenants. Not to be confused
/// with a future end-customer identity — see docs/architecture/authorization.md.
/// </summary>
public sealed class TenantUser : AuditableEntity, ITenantScoped, ILockableAccount
{
    public Guid TenantId { get; private set; }

    public string Email { get; private set; }

    /// <summary>Uppercase-invariant form of <see cref="Email"/> — the only column uniqueness/lookup uses.</summary>
    public string NormalizedEmail { get; private set; }

    public string PasswordHash { get; private set; }

    public string DisplayName { get; private set; }

    public UserStatus Status { get; private set; }

    public int FailedLoginCount { get; private set; }

    public DateTimeOffset? LockedUntil { get; private set; }

    public DateTimeOffset? LastLoginAt { get; private set; }

    private TenantUser()
    {
        Email = string.Empty;
        NormalizedEmail = string.Empty;
        PasswordHash = string.Empty;
        DisplayName = string.Empty;
    }

    private TenantUser(Guid id, DateTimeOffset createdAt, Guid tenantId, string email, string normalizedEmail, string passwordHash, string displayName)
        : base(id, createdAt)
    {
        TenantId = tenantId;
        Email = email;
        NormalizedEmail = normalizedEmail;
        PasswordHash = passwordHash;
        DisplayName = displayName;
        Status = UserStatus.Invited;
    }

    public static TenantUser Create(
        Guid id,
        DateTimeOffset createdAt,
        Guid tenantId,
        string email,
        string passwordHash,
        string displayName)
    {
        if (tenantId == Guid.Empty)
        {
            throw DomainException.Validation("A tenant user must belong to a tenant.");
        }

        var normalizedEmail = ValidateEmail(email);

        return new TenantUser(id, createdAt, tenantId, EmailFormat.Normalize(email), normalizedEmail, passwordHash, displayName.Trim());
    }

    public void Activate(DateTimeOffset now)
    {
        Status = UserStatus.Active;
        MarkUpdated(now);
    }

    public void Suspend(DateTimeOffset now)
    {
        Status = UserStatus.Suspended;
        MarkUpdated(now);
    }

    public void Disable(DateTimeOffset now)
    {
        Status = UserStatus.Disabled;
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

    /// <summary>
    /// Whether the account can currently authenticate, independent of credential
    /// correctness and independent of tenant status — callers must additionally
    /// check the owning <c>Tenant</c>'s status, which is authoritative.
    /// </summary>
    public bool CanAuthenticate(DateTimeOffset now) => Status == UserStatus.Active && !IsLocked(now);

    public bool IsLocked(DateTimeOffset now) => LockedUntil.HasValue && LockedUntil.Value > now;

    public void RegisterFailedLogin(DateTimeOffset now, int lockoutThreshold, TimeSpan lockoutDuration)
    {
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

    private static string ValidateEmail(string email)
    {
        if (string.IsNullOrWhiteSpace(email))
        {
            throw DomainException.Validation("Email is required.");
        }

        var normalized = EmailFormat.NormalizeForComparison(email);
        if (!EmailFormat.IsValid(EmailFormat.Normalize(email)))
        {
            throw DomainException.Validation("Email is not a valid address.");
        }

        return normalized;
    }
}
