using AccessControl.Domain;

namespace AccessControl;

/// <summary>
/// Resolves what an account may actually do, from the roles it holds right now.
///
/// Permissions are never read from the access token: revoking a role has to take
/// effect on the next request, not the next login (docs/authorization.md
/// section 6). The result is cached for the lifetime of one request only, which
/// keeps a handful of authorization checks on the same request down to a single
/// query without ever outliving the change it would hide.
/// </summary>
public interface IEffectivePermissionResolver
{
    Task<IReadOnlyCollection<string>> ResolveAsync(Guid userId, CancellationToken cancellationToken);

    Task<IReadOnlyCollection<string>> GetRoleNamesAsync(Guid userId, CancellationToken cancellationToken);

    Task<bool> HasPermissionAsync(Guid userId, string permissionKey, CancellationToken cancellationToken);
}

internal sealed class EffectivePermissionResolver : IEffectivePermissionResolver
{
    private readonly IRoleRepository _roles;
    private readonly Dictionary<Guid, IReadOnlyCollection<Role>> _perRequestCache = [];

    public EffectivePermissionResolver(IRoleRepository roles)
    {
        _roles = roles;
    }

    public async Task<IReadOnlyCollection<string>> ResolveAsync(Guid userId, CancellationToken cancellationToken)
    {
        var roles = await LoadAsync(userId, cancellationToken);

        return roles
            .SelectMany(role => role.Permissions.Select(permission => permission.PermissionKey))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(key => key, StringComparer.Ordinal)
            .ToArray();
    }

    public async Task<IReadOnlyCollection<string>> GetRoleNamesAsync(Guid userId, CancellationToken cancellationToken)
    {
        var roles = await LoadAsync(userId, cancellationToken);
        return roles.Select(role => role.Name).OrderBy(name => name, StringComparer.Ordinal).ToArray();
    }

    public async Task<bool> HasPermissionAsync(Guid userId, string permissionKey, CancellationToken cancellationToken)
    {
        var roles = await LoadAsync(userId, cancellationToken);
        return roles.Any(role => role.HasPermission(permissionKey));
    }

    private async Task<IReadOnlyCollection<Role>> LoadAsync(Guid userId, CancellationToken cancellationToken)
    {
        if (_perRequestCache.TryGetValue(userId, out var cached))
        {
            return cached;
        }

        var roles = await _roles.GetRolesForUserAsync(userId, cancellationToken);
        _perRequestCache[userId] = roles;
        return roles;
    }
}
