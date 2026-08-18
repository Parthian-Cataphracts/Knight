namespace Knight.Application.Authorization;

/// <summary>
/// Implemented once per module to declare the permissions it owns. Collected and
/// registered into <see cref="IPermissionCatalog"/> during application startup —
/// see Knight.Api composition root.
/// </summary>
public interface IPermissionProvider
{
    IReadOnlyCollection<Permission> Permissions { get; }
}
