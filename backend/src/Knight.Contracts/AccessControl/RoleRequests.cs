namespace Knight.Contracts.AccessControl;

public sealed record CreateRoleRequest
{
    public required string Name { get; init; }

    public IReadOnlyCollection<string> PermissionKeys { get; init; } = [];
}

public sealed record UpdateRoleRequest
{
    public required string Name { get; init; }
}

public sealed record SetRolePermissionsRequest
{
    public required IReadOnlyCollection<string> PermissionKeys { get; init; }
}
