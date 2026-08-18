using AccessControl.Domain;
using Knight.Application.Abstractions.Time;

namespace AccessControl;

/// <summary>
/// Reconciles the six system roles from docs/authorization.md section 1 with
/// their definitions in code. Their permission sets live in code because a
/// deployment must be able to ship a change to them; the rows themselves are
/// ordinary data that operators can sit their own roles alongside.
///
/// Seeding is idempotent and runs the reconciliation every time, so adding a
/// permission to a role definition ships with the deployment rather than
/// needing a manual data fix.
///
/// It deliberately creates no accounts. The first administrator is made by the
/// Knight.Bootstrap tool, by hand, with a password read interactively — there is
/// no registration endpoint and no credential in configuration
/// (docs/security/README.md).
/// </summary>
public interface IControlPlaneAccessSeeder
{
    Task SeedAsync(CancellationToken cancellationToken);
}

internal sealed class ControlPlaneAccessSeeder : IControlPlaneAccessSeeder
{
    private readonly IRoleRepository _roles;
    private readonly IDateTimeProvider _clock;

    public ControlPlaneAccessSeeder(IRoleRepository roles, IDateTimeProvider clock)
    {
        _roles = roles;
        _clock = clock;
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
    }
}
