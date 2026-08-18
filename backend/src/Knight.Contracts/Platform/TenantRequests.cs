namespace Knight.Contracts.Platform;

public sealed record CreateTenantRequest
{
    public required string Name { get; init; }

    public required string Slug { get; init; }

    public required string TimeZone { get; init; }

    public required string DefaultCurrency { get; init; }
}

public sealed record UpdateTenantRequest
{
    public required string Name { get; init; }

    public required string TimeZone { get; init; }

    public required string DefaultCurrency { get; init; }
}

public sealed record AddTenantDomainRequest
{
    public required string Host { get; init; }

    public required string Type { get; init; }

    public bool MakePrimary { get; init; }
}
