namespace Identity.Domain;

/// <summary>
/// Common shape shared by <see cref="PlatformAdmin"/> and <see cref="TenantUser"/>
/// for credential/lockout handling, letting authentication services share one
/// verification helper instead of duplicating the logic per principal type.
/// </summary>
public interface ILockableAccount
{
    Guid Id { get; }
    string PasswordHash { get; }
    UserStatus Status { get; }
    int FailedLoginCount { get; }
    DateTimeOffset? LockedUntil { get; }

    bool CanAuthenticate(DateTimeOffset now);
    bool IsLocked(DateTimeOffset now);
    void RegisterFailedLogin(DateTimeOffset now, int lockoutThreshold, TimeSpan lockoutDuration);
    void RegisterSuccessfulLogin(DateTimeOffset now);

    /// <summary>Administratively clears lockout state without touching the password or <c>LastLoginAt</c>.</summary>
    void Unlock(DateTimeOffset now);

    void ChangePasswordHash(string passwordHash, DateTimeOffset now);
}
