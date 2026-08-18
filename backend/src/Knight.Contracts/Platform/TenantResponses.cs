namespace Knight.Contracts.Platform;

public sealed record TenantDomainResponse
{
    public required Guid Id { get; init; }

    public required string Host { get; init; }

    public required string Type { get; init; }

    public required bool IsPrimary { get; init; }

    public required string VerificationStatus { get; init; }
}

public sealed record TenantResponse
{
    public required Guid Id { get; init; }

    public required string Name { get; init; }

    public required string Slug { get; init; }

    public required string Status { get; init; }

    public required string TimeZone { get; init; }

    public required string DefaultCurrency { get; init; }

    public required IReadOnlyCollection<TenantDomainResponse> Domains { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset? UpdatedAt { get; init; }
}

/// <summary>
/// Safe, minimal metadata about the caller's own current tenant. Deliberately
/// excludes anything a tenant user should not see about platform configuration.
/// </summary>
public sealed record CurrentTenantResponse
{
    public required Guid Id { get; init; }

    public required string Name { get; init; }

    public required string Slug { get; init; }

    public required IReadOnlyCollection<string> EnabledFeatures { get; init; }
}
