using System.Security.Cryptography;
using AccessControl.Abstractions;
using AccessControl.Domain;
using Knight.Application.Abstractions.ControlPlane;
using Knight.Application.Abstractions.Time;
using Knight.Application.Exceptions;

namespace AccessControl;

/// <summary>
/// Administering accounts and roles.
///
/// The shape of this class follows from one rule: **an administrator never
/// learns another account's password**. A new account is created with a
/// generated one-time password that is returned once and stored only as a hash;
/// a forgotten password is replaced rather than recovered. That is what keeps
/// "an administrator can reset an account" from also meaning "an administrator
/// can silently become that account and act as them".
///
/// Every operation is audited, including the ones that look harmless. Resetting
/// somebody's second factor is exactly what an attacker holding an
/// administrator's session would do first.
/// </summary>
internal sealed class AccountAdministration : IAccountAdministration
{
    /// <summary>
    /// Long enough that a generated password is not worth attacking, short
    /// enough that somebody can read it down a phone line once.
    /// </summary>
    private const int TemporaryPasswordBytes = 18;

    private readonly IControlPlaneUserRepository _users;
    private readonly IRoleRepository _roles;
    private readonly IControlPlanePasswordHasher _passwords;
    private readonly ISecureTokenFactory _tokens;
    private readonly IAccountInvitationSender _invitations;
    private readonly IAuditTrail _audit;
    private readonly IDateTimeProvider _clock;
    private readonly AccessControlOptions _options;

    public AccountAdministration(
        IControlPlaneUserRepository users,
        IRoleRepository roles,
        IControlPlanePasswordHasher passwords,
        ISecureTokenFactory tokens,
        IAccountInvitationSender invitations,
        IAuditTrail audit,
        IDateTimeProvider clock,
        Microsoft.Extensions.Options.IOptions<AccessControlOptions> options)
    {
        _users = users;
        _roles = roles;
        _passwords = passwords;
        _tokens = tokens;
        _invitations = invitations;
        _audit = audit;
        _clock = clock;
        _options = options.Value;
    }

    public async Task<AccountCreationResult> CreateAsync(
        string email,
        string displayName,
        Guid? customerId,
        IReadOnlyCollection<Guid> roleIds,
        CancellationToken cancellationToken)
    {
        var address = (email ?? string.Empty).Trim();

        // Checked here as well as in the aggregate: the duplicate lookup needs a
        // normalised value, and normalising an absent one would silently look up
        // the empty address.
        if (address.Length == 0)
        {
            throw new ValidationException(new Dictionary<string, string[]>
            {
                ["email"] = ["An account needs an email address."],
            });
        }

        if (await _users.ExistsWithEmailAsync(address.ToUpperInvariant(), cancellationToken))
        {
            throw new ConflictException("An account with that email address already exists.");
        }

        var now = _clock.UtcNow;
        var password = GeneratePassword();
        var hash = _passwords.Hash(password);

        var user = customerId is { } customer
            ? ControlPlaneUser.CreateCustomerUser(Guid.NewGuid(), now, customer, address, displayName, hash)
            : ControlPlaneUser.CreatePlatformStaff(Guid.NewGuid(), now, address, displayName, hash);

        // Which of the two paths this is depends on whether mail can leave this
        // deployment. Where it can, the account stays Invited and its holder
        // sets their own password from the emailed link — nobody else ever knows
        // it. Where it cannot, the administrator is handed the generated
        // password and the account is activated, because an account that could
        // not sign in with the password it was just given would look broken.
        var invitation = _invitations.CanSend ? _tokens.Generate() : null;

        if (invitation is null)
        {
            user.Activate(now);
        }
        else
        {
            user.BeginActivation(invitation.Hash, now.Add(_options.InvitationLifetime), now);
        }

        await _users.AddAsync(user, cancellationToken);

        // Roles are assigned in the same unit of work: an account that exists
        // with no roles can sign in and see nothing, which reads as a broken
        // account rather than an unfinished one.
        await AssignAsync(user, roleIds, now, cancellationToken);

        await _users.SaveChangesAsync(cancellationToken);

        await _audit.RecordAsync(
            "user.created",
            "ControlPlaneUser",
            user.Id.ToString(),
            customerId,
            cancellationToken,
            // The generated password is deliberately absent: the audit trail is
            // the last place a credential should end up.
            // Neither the generated password nor the invitation token is here:
            // the audit trail is the last place a credential should end up.
            newValue: new { user.Email, user.DisplayName, RoleCount = roleIds.Count, invited = invitation is not null });

        if (invitation is null)
        {
            return new AccountCreationResult(user, password, false);
        }

        var sent = await _invitations.SendAsync(user, invitation.RawValue, cancellationToken);

        if (!sent)
        {
            // The mail did not leave. Rather than leaving an account nobody can
            // ever reach, the invitation is abandoned and the administrator gets
            // the one-time password after all — with the account activated so it
            // works.
            user.CompleteActivation(hash, now);
            await _users.SaveChangesAsync(cancellationToken);

            return new AccountCreationResult(user, password, false);
        }

        return new AccountCreationResult(user, null, true);
    }

    public async Task<ControlPlaneUser> RenameAsync(Guid userId, string displayName, CancellationToken cancellationToken)
    {
        var user = await RequireAsync(userId, cancellationToken);

        user.Rename(displayName, _clock.UtcNow);

        await _users.SaveChangesAsync(cancellationToken);

        await _audit.RecordAsync(
            "user.renamed", "ControlPlaneUser", user.Id.ToString(), user.CustomerId, cancellationToken,
            newValue: new { user.DisplayName });

        return user;
    }

    public async Task<ControlPlaneUser> SetActiveAsync(Guid userId, bool active, CancellationToken cancellationToken)
    {
        var user = await RequireAsync(userId, cancellationToken);
        var now = _clock.UtcNow;

        if (active)
        {
            user.Activate(now);
        }
        else
        {
            user.Suspend(now);
        }

        await _users.SaveChangesAsync(cancellationToken);

        await _audit.RecordAsync(
            active ? "user.activated" : "user.suspended",
            "ControlPlaneUser",
            user.Id.ToString(),
            user.CustomerId,
            cancellationToken);

        return user;
    }

    public async Task<ControlPlaneUser> SetRolesAsync(
        Guid userId,
        IReadOnlyCollection<Guid> roleIds,
        CancellationToken cancellationToken)
    {
        var user = await RequireAsync(userId, cancellationToken);
        var now = _clock.UtcNow;

        // Removed first, then added, inside one save: the account never observes
        // an intermediate state with neither set of roles.
        foreach (var assignment in user.Roles.Where(role => !roleIds.Contains(role.RoleId)).ToArray())
        {
            _users.RemoveAssignment(assignment);
            user.RemoveRole(assignment.RoleId, now);
        }

        await AssignAsync(user, roleIds, now, cancellationToken);

        await _users.SaveChangesAsync(cancellationToken);

        await _audit.RecordAsync(
            "user.roles.changed",
            "ControlPlaneUser",
            user.Id.ToString(),
            user.CustomerId,
            cancellationToken,
            newValue: new { RoleIds = roleIds });

        return user;
    }

    public async Task<ControlPlaneUser> ResetMfaAsync(Guid userId, CancellationToken cancellationToken)
    {
        var user = await RequireAsync(userId, cancellationToken);

        user.DisableMfa(_clock.UtcNow);

        await _users.SaveChangesAsync(cancellationToken);

        // Audited prominently. This is the step an attacker holding an
        // administrator's session takes before taking over an account.
        await _audit.RecordAsync(
            "user.mfa.reset", "ControlPlaneUser", user.Id.ToString(), user.CustomerId, cancellationToken);

        return user;
    }

    public async Task<string> ResetPasswordAsync(Guid userId, CancellationToken cancellationToken)
    {
        var user = await RequireAsync(userId, cancellationToken);
        var password = GeneratePassword();

        user.ChangePasswordHash(_passwords.Hash(password), _clock.UtcNow);

        await _users.SaveChangesAsync(cancellationToken);

        await _audit.RecordAsync(
            "user.password.reset", "ControlPlaneUser", user.Id.ToString(), user.CustomerId, cancellationToken);

        return password;
    }

    public async Task<ControlPlaneUser> CompleteActivationAsync(
        string token,
        string password,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(password))
        {
            throw new ValidationException(new Dictionary<string, string[]>
            {
                ["token"] = ["An activation token and a password are required."],
            });
        }

        // Looked up by hash: the plaintext token exists only in the link, and
        // this table holds nothing anybody could replay.
        var user = await _users.FindByActivationTokenAsync(_tokens.Hash(token.Trim()), cancellationToken)
            ?? throw new ConflictException("This invitation is no longer valid. Ask an administrator for a new one.");

        user.CompleteActivation(_passwords.Hash(password), _clock.UtcNow);
        await _users.SaveChangesAsync(cancellationToken);

        await _audit.RecordAsync(
            "user.activated",
            "ControlPlaneUser",
            user.Id.ToString(),
            user.CustomerId,
            cancellationToken,
            newValue: new { user.Email });

        return user;
    }

    public async Task<bool> ResendInvitationAsync(Guid userId, CancellationToken cancellationToken)
    {
        if (!_invitations.CanSend)
        {
            return false;
        }

        var user = await RequireAsync(userId, cancellationToken);
        var now = _clock.UtcNow;
        var invitation = _tokens.Generate();

        // Re-inviting replaces the outstanding token rather than adding one:
        // two live invitations to the same account are two ways in.
        user.BeginActivation(invitation.Hash, now.Add(_options.InvitationLifetime), now);
        await _users.SaveChangesAsync(cancellationToken);

        var sent = await _invitations.SendAsync(user, invitation.RawValue, cancellationToken);

        await _audit.RecordAsync(
            "user.invitation.sent",
            "ControlPlaneUser",
            user.Id.ToString(),
            user.CustomerId,
            cancellationToken,
            newValue: new { user.Email, sent });

        return sent;
    }

    public async Task<Role> CreateRoleAsync(
        string name,
        string description,
        RoleScope scope,
        Guid? customerId,
        IReadOnlyCollection<string> permissions,
        CancellationToken cancellationToken)
    {
        var now = _clock.UtcNow;
        var role = Role.CreateCustom(Guid.NewGuid(), now, name, scope, customerId, description);

        // The aggregate refuses a platform permission on a customer-scoped role,
        // so an over-broad role cannot be created by asking nicely.
        role.ReplacePermissions(permissions, now);

        await _roles.AddAsync(role, cancellationToken);
        await _roles.SaveChangesAsync(cancellationToken);

        await _audit.RecordAsync(
            "role.created", "Role", role.Id.ToString(), customerId, cancellationToken,
            newValue: new { role.Name, Scope = scope.ToString(), Permissions = permissions });

        return role;
    }

    public async Task<Role> SetRolePermissionsAsync(
        Guid roleId,
        IReadOnlyCollection<string> permissions,
        CancellationToken cancellationToken)
    {
        var role = await _roles.GetByIdAsync(roleId, cancellationToken)
            ?? throw new NotFoundException("Role", roleId);

        // System roles are the floor the product relies on: editing one could
        // remove a permission every screen assumes exists.
        role.ReplacePermissions(permissions, _clock.UtcNow);

        await _roles.SaveChangesAsync(cancellationToken);

        await _audit.RecordAsync(
            "role.permissions.changed", "Role", role.Id.ToString(), role.CustomerId, cancellationToken,
            newValue: new { role.Name, Permissions = permissions });

        return role;
    }

    private async Task AssignAsync(
        ControlPlaneUser user,
        IReadOnlyCollection<Guid> roleIds,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        foreach (var roleId in roleIds.Distinct())
        {
            if (user.Roles.Any(assignment => assignment.RoleId == roleId))
            {
                continue;
            }

            var role = await _roles.GetByIdAsync(roleId, cancellationToken)
                ?? throw new NotFoundException("Role", roleId);

            // The aggregate refuses to put a platform role on a customer account
            // and vice versa, which is the check that matters here.
            _users.RegisterNewAssignment(user.AssignRole(Guid.NewGuid(), role, now));
        }
    }

    private async Task<ControlPlaneUser> RequireAsync(Guid userId, CancellationToken cancellationToken) =>
        await _users.GetByIdAsync(userId, cancellationToken)
        ?? throw new NotFoundException("Account", userId);

    /// <summary>
    /// A one-time password. Base64url of cryptographic random bytes rather than
    /// anything word-shaped: it exists to be copied once and replaced, not
    /// remembered.
    /// </summary>
    private static string GeneratePassword() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(TemporaryPasswordBytes))
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');
}
