namespace Knight.Application.Authorization;

/// <summary>
/// Central registry of permissions declared by modules. Modules register their
/// permissions at startup via <see cref="Register"/>; authorization and admin
/// tooling read the full set via <see cref="All"/>.
/// </summary>
public interface IPermissionCatalog
{
    IReadOnlyCollection<Permission> All { get; }

    void Register(IEnumerable<Permission> permissions);

    bool IsRegistered(Permission permission);
}
