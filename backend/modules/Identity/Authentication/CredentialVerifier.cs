using Identity.Abstractions;
using Identity.Domain;
using Identity.Options;

namespace Identity.Authentication;

/// <summary>
/// Shared password/lockout verification logic for both Platform and Tenant
/// login, so the two authentication services (kept separate per
/// docs/architecture/authorization.md) do not duplicate credential handling.
/// </summary>
internal sealed class CredentialVerifier
{
    private static string? s_dummyHash;
    private static readonly Lock DummyHashLock = new();

    private readonly IPasswordHasher _passwordHasher;
    private readonly LockoutOptions _lockoutOptions;

    public CredentialVerifier(IPasswordHasher passwordHasher, LockoutOptions lockoutOptions)
    {
        _passwordHasher = passwordHasher;
        _lockoutOptions = lockoutOptions;
    }

    /// <summary>
    /// Verifies a password against a possibly-null account, applying lockout
    /// rules and updating the account's failed/successful login state
    /// in-memory (callers must still persist it). Performs a dummy hash
    /// verification when <paramref name="account"/> is null so an unknown
    /// email does not complete measurably faster than a known one.
    /// </summary>
    public LoginOutcome Verify(ILockableAccount? account, string password, DateTimeOffset now)
    {
        if (account is null)
        {
            _passwordHasher.Verify(password, GetDummyHash());
            return LoginOutcome.InvalidCredentials;
        }

        if (account.IsLocked(now))
        {
            // Still perform the verify so a locked account's response timing
            // matches an active account's — only the outcome differs.
            _passwordHasher.Verify(password, account.PasswordHash);
            return LoginOutcome.AccountLocked;
        }

        if (!account.CanAuthenticate(now))
        {
            _passwordHasher.Verify(password, account.PasswordHash);
            return LoginOutcome.AccountUnavailable;
        }

        if (!_passwordHasher.Verify(password, account.PasswordHash))
        {
            account.RegisterFailedLogin(now, _lockoutOptions.FailedAttemptThreshold, _lockoutOptions.LockoutDuration);
            return LoginOutcome.InvalidCredentials;
        }

        account.RegisterSuccessfulLogin(now);
        return LoginOutcome.Success;
    }

    /// <summary>Rehashes and returns the new hash if the stored hash's work factor is outdated; otherwise null.</summary>
    public string? RehashIfNeeded(string currentHash, string plaintextPassword) =>
        _passwordHasher.NeedsRehash(currentHash) ? _passwordHasher.Hash(plaintextPassword) : null;

    private string GetDummyHash()
    {
        if (s_dummyHash is not null)
        {
            return s_dummyHash;
        }

        lock (DummyHashLock)
        {
            s_dummyHash ??= _passwordHasher.Hash("enumeration-resistance-dummy-password");
            return s_dummyHash;
        }
    }
}
