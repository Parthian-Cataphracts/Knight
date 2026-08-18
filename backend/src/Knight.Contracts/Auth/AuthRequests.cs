namespace Knight.Contracts.Auth;

public sealed record LoginRequest
{
    public required string Email { get; init; }

    public required string Password { get; init; }
}

public sealed record ChangePasswordRequest
{
    public required string CurrentPassword { get; init; }

    public required string NewPassword { get; init; }
}
