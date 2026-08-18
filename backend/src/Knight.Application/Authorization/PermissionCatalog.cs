using System.Collections.Concurrent;

namespace Knight.Application.Authorization;

/// <summary>
/// Default in-memory <see cref="IPermissionCatalog"/>. Registered as a singleton;
/// modules populate it during application startup. Re-registering the exact same
/// key with different metadata (a copy/paste mistake between two providers) fails
/// fast at startup rather than silently keeping whichever registered first.
/// </summary>
public sealed class PermissionCatalog : IPermissionCatalog
{
    private readonly ConcurrentDictionary<string, Permission> _permissions = new(StringComparer.Ordinal);

    public IReadOnlyCollection<Permission> All => _permissions.Values.ToArray();

    public void Register(IEnumerable<Permission> permissions)
    {
        foreach (var permission in permissions)
        {
            var stored = _permissions.GetOrAdd(permission.Key, permission);

            if (stored.Description != permission.Description || stored.Module != permission.Module)
            {
                throw new InvalidOperationException(
                    $"Permission '{permission.Key}' was registered twice with conflicting metadata. " +
                    "Each permission key must be owned by exactly one module registration.");
            }
        }
    }

    public bool IsRegistered(Permission permission) => _permissions.ContainsKey(permission.Key);
}
