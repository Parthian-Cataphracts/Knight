namespace Knight.Contracts.AccessControl;

public sealed record RoleResponse
{
    public required Guid Id { get; init; }

    public required string Name { get; init; }

    public required int PermissionCount { get; init; }

    public required int AssignedUserCount { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }
}

public sealed record RoleDetailResponse
{
    public required Guid Id { get; init; }

    public required string Name { get; init; }

    public required IReadOnlyCollection<string> PermissionKeys { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }
}

public sealed record PermissionResponse
{
    public required string Key { get; init; }

    public string? Description { get; init; }

    public string? Module { get; init; }
}
