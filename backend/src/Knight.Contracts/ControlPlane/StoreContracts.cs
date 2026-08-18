namespace Knight.Contracts.ControlPlane;

public sealed record CreateStoreRequest
{
    public required Guid CustomerId { get; init; }

    public required string Name { get; init; }

    public required string Slug { get; init; }

    public required string PrimaryDomain { get; init; }

    /// <summary>Development, Staging or Production.</summary>
    public required string Environment { get; init; }

    /// <summary>SharedManaged, DedicatedManaged or CustomerManaged.</summary>
    public required string HostingModel { get; init; }
}

public sealed record UpdateStoreRequest
{
    public required string Name { get; init; }

    public required string PrimaryDomain { get; init; }

    public Guid? ServerId { get; init; }
}

public sealed record StoreCredentialResponse
{
    public required Guid Id { get; init; }

    public required string ClientId { get; init; }

    /// <summary>Active, GracePeriod, Expired or Revoked, evaluated against the current time.</summary>
    public required string State { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset? ExpiresAt { get; init; }

    public DateTimeOffset? RotatedAt { get; init; }

    public DateTimeOffset? RevokedAt { get; init; }

    public DateTimeOffset? LastUsedAt { get; init; }
}

/// <summary>
/// The one and only time a client secret is ever returned. It is not stored in
/// plaintext anywhere and cannot be retrieved again — a lost secret is replaced
/// by rotating the credential.
/// </summary>
public sealed record IssuedStoreCredentialResponse
{
    public required Guid Id { get; init; }

    public required string ClientId { get; init; }

    public required string ClientSecret { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset? ExpiresAt { get; init; }
}

public sealed record StoreResponse
{
    public required Guid Id { get; init; }

    public required Guid CustomerId { get; init; }

    /// <summary>The owning customer's name, so a store list reads without a second call per row.</summary>
    public required string CustomerName { get; init; }

    public required string Name { get; init; }

    public required string Slug { get; init; }

    public required string PrimaryDomain { get; init; }

    public required string Environment { get; init; }

    public required string HostingModel { get; init; }

    public required string Status { get; init; }

    public required string IntegrationStatus { get; init; }

    public string? ApplicationVersion { get; init; }

    public DateTimeOffset? LastSeenAt { get; init; }

    public Guid? ServerId { get; init; }

    /// <summary>
    /// Null until feature delivery exists (phase 3.5). Zero would claim the
    /// store has nothing installed, which is a different statement from "not
    /// knowable yet".
    /// </summary>
    public int? InstalledFeatureCount { get; init; }

    public required IReadOnlyCollection<StoreCredentialResponse> Credentials { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset? UpdatedAt { get; init; }
}
