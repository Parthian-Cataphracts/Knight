namespace Knight.Contracts.AccessControl;

public sealed record CreateStaffRequest
{
    public required string Email { get; init; }

    public required string DisplayName { get; init; }

    public required string InitialPassword { get; init; }

    public IReadOnlyCollection<Guid> RoleIds { get; init; } = [];
}

public sealed record ReplaceStaffRolesRequest
{
    public required IReadOnlyCollection<Guid> RoleIds { get; init; }
}
