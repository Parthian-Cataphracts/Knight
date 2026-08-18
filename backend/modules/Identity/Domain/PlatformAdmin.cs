using Knight.Domain.Common;
using Knight.Domain.Exceptions;

namespace Identity.Domain;

/// <summary>
/// A Platform Super Admin account. Conceptually and structurally separate from
/// tenant users — see docs/architecture/authorization.md.
/// </summary>
public sealed class PlatformAdmin : AuditableEntity, ILockableAccount
{
    public string Email { get; private set; }

    /// <summary>Uppercase-invariant form of <see cref="Email"/> — the only column uniqueness/lookup uses.</summary>
    public string NormalizedEmail { get; private set; }

    public string PasswordHash { get; private set; }

    public string DisplayName { get; private set; }

    public UserStatus Status { get; private set; }

    public int FailedLoginCount { get; private set; }

    public DateTimeOffset? LockedUntil { get; private set; }

    public DateTimeOffset? LastLoginAt { get; private set; }

    private PlatformAdmin()
    {
        Email = string.Empty;
        NormalizedEmail = string.Empty;
        PasswordHash = string.Empty;
        DisplayName = string.Empty;
    }

    private PlatformAdmin(Guid id, DateTimeOffset createdAt, string email, string normalizedEmail, string passwordHash, string displayName)
        : base(id, createdAt)
    {
        Email = email;
        NormalizedEmail = normalizedEmail;
        PasswordHash = passwordHash;
        DisplayName = displayName;
        Status = UserStatus.Invited;
    }

    public static PlatformAdmin Create(Guid id, DateTimeOffset createdAt, string email, string passwordHash, string displayName)
    {
        var normalizedEmail = ValidateEmail(email);

        if (string.IsNullOrWhiteSpace(passwordHash))
        {
            throw DomainException.Validation("Password hash is required.");
        }

        return new PlatformAdmin(id, createdAt, EmailFormat.Normalize(email), normalizedEmail, passwordHash, displayName.Trim());
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

    /// <summary>Whether the account can currently authenticate at all, independent of credential correctness.</summary>
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
