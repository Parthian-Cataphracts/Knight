namespace Knight.Contracts.Auth;

public sealed record LoginResponse
{
    public required string AccessToken { get; init; }

    public required string TokenType { get; init; }

    public required int ExpiresInSeconds { get; init; }
}

public sealed record CurrentPlatformAdminResponse
{
    public required Guid Id { get; init; }

    public required string Email { get; init; }

    public required string DisplayName { get; init; }

    public required string Status { get; init; }
}

public sealed record CurrentTenantUserResponse
{
    public required Guid Id { get; init; }

    public required Guid TenantId { get; init; }

    public required string Email { get; init; }

    public required string DisplayName { get; init; }

    public required string Status { get; init; }

    public required IReadOnlyCollection<string> EffectivePermissions { get; init; }
}
