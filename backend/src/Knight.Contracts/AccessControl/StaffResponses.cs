namespace Knight.Contracts.AccessControl;

public sealed record StaffResponse
{
    public required Guid Id { get; init; }

    public required string Email { get; init; }

    public required string DisplayName { get; init; }

    public required bool IsEnabled { get; init; }

    public required bool IsLocked { get; init; }

    public required IReadOnlyCollection<Guid> RoleIds { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset? LastLoginAt { get; init; }
}
