using AccessControl.Abstractions;
using AccessControl.Domain;
using Knight.Application.Abstractions.Time;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AccessControl;

/// <summary>
/// Brings a deployment up to a usable access model: the six system roles from
/// docs/authorization.md, and — only when no platform account exists yet — the
/// bootstrap administrator, without whom nobody could ever sign in.
///
/// Seeding is idempotent and reconciles system-role permissions on every start,
/// so adding a permission to a role definition ships with the deployment rather
/// than needing a manual data fix.
/// </summary>
public interface IControlPlaneAccessSeeder
{
    Task SeedAsync(CancellationToken cancellationToken);
}

internal sealed class ControlPlaneAccessSeeder : IControlPlaneAccessSeeder
{
    private readonly IRoleRepository _roles;
    private readonly IControlPlaneUserRepository _users;
    private readonly IControlPlanePasswordHasher _passwordHasher;
    private readonly IDateTimeProvider _clock;
    private readonly AccessControlOptions _options;
    private readonly ILogger<ControlPlaneAccessSeeder> _logger;

    public ControlPlaneAccessSeeder(
        IRoleRepository roles,
        IControlPlaneUserRepository users,
        IControlPlanePasswordHasher passwordHasher,
        IDateTimeProvider clock,
        IOptions<AccessControlOptions> options,
        ILogger<ControlPlaneAccessSeeder> logger)
    {
        _roles = roles;
        _users = users;
        _passwordHasher = passwordHasher;
        _clock = clock;
        _options = options.Value;
        _logger = logger;
    }

    public async Task SeedAsync(CancellationToken cancellationToken)
    {
        var now = _clock.UtcNow;

        foreach (var definition in SystemRoles.All)
        {
            var existing = await _roles.GetByNameAsync(
                definition.Name.ToUpperInvariant(),
                definition.Scope,
                customerId: null,
                cancellationToken);

            if (existing is null)
            {
                var role = Role.CreateSystem(Guid.NewGuid(), now, definition.Name, definition.Scope, definition.Description);
                role.SeedPermissions(definition.Permissions, now);
                await _roles.AddAsync(role, cancellationToken);
            }
            else
            {
                existing.SeedPermissions(definition.Permissions, now);
            }
        }

        await _roles.SaveChangesAsync(cancellationToken);

        await SeedBootstrapAdminAsync(now, cancellationToken);
    }

    private async Task SeedBootstrapAdminAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        var bootstrap = _options.BootstrapAdmin;
        if (bootstrap is null || string.IsNullOrWhiteSpace(bootstrap.Email) || string.IsNullOrWhiteSpace(bootstrap.Password))
        {
            return;
        }

        var normalizedEmail = EmailAddress.NormalizeForComparison(bootstrap.Email);
        if (await _users.ExistsWithEmailAsync(normalizedEmail, cancellationToken))
        {
            return;
        }

        var superAdmin = await _roles.GetByNameAsync(
            SystemRoles.SuperAdmin.ToUpperInvariant(),
            RoleScope.Platform,
            customerId: null,
            cancellationToken);

        if (superAdmin is null)
        {
            throw new InvalidOperationException("The SuperAdmin role must be seeded before the bootstrap administrator.");
        }

        var user = ControlPlaneUser.CreatePlatformStaff(
            Guid.NewGuid(),
            now,
            bootstrap.Email,
            bootstrap.DisplayName,
            _passwordHasher.Hash(bootstrap.Password));

        user.Activate(now);
        var assignment = user.AssignRole(Guid.NewGuid(), superAdmin, now);

        await _users.AddAsync(user, cancellationToken);
        _users.RegisterNewAssignment(assignment);
        await _users.SaveChangesAsync(cancellationToken);

        // The account holds SuperAdmin, so it must enrol a second factor before
        // it can do anything beyond enrolment — see ControlPlaneAuthenticationService.
        _logger.LogInformation(
            "Seeded the bootstrap platform administrator {Email}. It must enrol MFA on first sign-in.",
            user.Email);
    }
}
