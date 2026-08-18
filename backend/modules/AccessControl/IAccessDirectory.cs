using AccessControl.Domain;

namespace AccessControl;

/// <summary>
/// Read access to accounts and roles for the dashboard's "users and access"
/// screen. Deliberately read-only in this phase: creating and editing accounts
/// through the API is a write path with its own delegation rules, and shipping
/// the reads first lets the screen work without inventing those rules in a hurry.
/// </summary>
public interface IAccessDirectory
{
    Task<PagedResult<ControlPlaneUser>> ListUsersAsync(
        int page,
        int pageSize,
        Guid? customerId,
        string? search,
        CancellationToken cancellationToken);

    Task<IReadOnlyCollection<Role>> ListRolesAsync(CancellationToken cancellationToken);
}

internal sealed class AccessDirectory : IAccessDirectory
{
    private const int MaxPageSize = 100;

    private readonly IControlPlaneUserRepository _users;
    private readonly IRoleRepository _roles;

    public AccessDirectory(IControlPlaneUserRepository users, IRoleRepository roles)
    {
        _users = users;
        _roles = roles;
    }

    public async Task<PagedResult<ControlPlaneUser>> ListUsersAsync(
        int page,
        int pageSize,
        Guid? customerId,
        string? search,
        CancellationToken cancellationToken)
    {
        var normalizedPage = page < 1 ? 1 : page;
        var normalizedSize = pageSize is < 1 or > MaxPageSize ? 25 : pageSize;

        var (items, total) = await _users.ListAsync(normalizedPage, normalizedSize, customerId, search, cancellationToken);
        return new PagedResult<ControlPlaneUser>(items, normalizedPage, normalizedSize, total);
    }

    public Task<IReadOnlyCollection<Role>> ListRolesAsync(CancellationToken cancellationToken) =>
        _roles.ListAsync(scope: null, cancellationToken);
}
