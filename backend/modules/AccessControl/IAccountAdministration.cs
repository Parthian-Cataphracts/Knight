using AccessControl.Domain;

namespace AccessControl;

/// <summary>
/// Managing accounts and what they may do.
///
/// Deliberately separate from <see cref="IControlPlaneAuthenticationService"/>:
/// authentication is what an account does for itself, and this is what an
/// administrator does to somebody else's. They have different permissions,
/// different audit actions and different failure modes, and merging them would
/// make it easy to expose one through the other.
///
/// Two rules run through all of it. An administrator never sets or reads
/// another account's password — a new account is created with a one-time
/// password the administrator hands over and the account must change — and a
/// customer-scoped administrator can only ever act within their own customer,
/// which the persistence filter enforces underneath rather than trusting this
/// layer to remember (docs/authorization.md §3).
/// </summary>
public interface IAccountAdministration
{
    /// <summary>
    /// Creates an account and returns it together with the one-time password it
    /// was created with.
    ///
    /// The password is returned exactly once, here, and never stored in a
    /// readable form — the account holds only its hash. An administrator who
    /// loses it resets the account rather than looking it up.
    /// </summary>
    Task<(ControlPlaneUser User, string TemporaryPassword)> CreateAsync(
        string email,
        string displayName,
        Guid? customerId,
        IReadOnlyCollection<Guid> roleIds,
        CancellationToken cancellationToken);

    Task<ControlPlaneUser> RenameAsync(Guid userId, string displayName, CancellationToken cancellationToken);

    /// <summary>Suspends or reactivates an account. Suspension takes effect on the next request, not the next login.</summary>
    Task<ControlPlaneUser> SetActiveAsync(Guid userId, bool active, CancellationToken cancellationToken);

    /// <summary>
    /// Replaces the account's roles with exactly this set.
    ///
    /// A replace rather than add and remove calls, because the set is what an
    /// administrator is actually deciding, and two-step editing leaves a window
    /// where an account holds neither the old roles nor the new ones.
    /// </summary>
    Task<ControlPlaneUser> SetRolesAsync(
        Guid userId,
        IReadOnlyCollection<Guid> roleIds,
        CancellationToken cancellationToken);

    /// <summary>
    /// Clears an account's second factor so it can enrol again.
    ///
    /// The one recovery path that exists, and an audited one: it is also how an
    /// attacker with an administrator's session would remove MFA from an account
    /// they wanted to take over.
    /// </summary>
    Task<ControlPlaneUser> ResetMfaAsync(Guid userId, CancellationToken cancellationToken);

    /// <summary>Issues a new one-time password, returned once.</summary>
    Task<string> ResetPasswordAsync(Guid userId, CancellationToken cancellationToken);

    /// <summary>Creates a role, in the scope its permissions allow.</summary>
    Task<Role> CreateRoleAsync(
        string name,
        string description,
        RoleScope scope,
        Guid? customerId,
        IReadOnlyCollection<string> permissions,
        CancellationToken cancellationToken);

    /// <summary>Replaces a role's permissions. System roles refuse this.</summary>
    Task<Role> SetRolePermissionsAsync(
        Guid roleId,
        IReadOnlyCollection<string> permissions,
        CancellationToken cancellationToken);
}
