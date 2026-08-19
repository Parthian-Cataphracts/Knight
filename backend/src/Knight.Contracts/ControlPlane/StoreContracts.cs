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

/// <summary>One health observation as the dashboard shows it.</summary>
public sealed record StoreHealthCheckResponse
{
    public required Guid Id { get; init; }

    public required DateTimeOffset CheckedAt { get; init; }

    /// <summary>Healthy, Degraded, Unhealthy or Unreachable.</summary>
    public required string Status { get; init; }

    /// <summary>Poll, Heartbeat or Handshake — whether KNIGHT asked or the store told.</summary>
    public required string Source { get; init; }

    public int? ResponseTimeMs { get; init; }

    public string? ReportedVersion { get; init; }

    /// <summary>The store's own dependency block, passed through untouched.</summary>
    public object? Dependencies { get; init; }

    /// <summary>Feature slugs the store reports as installed.</summary>
    public object? ReportedFeatures { get; init; }

    public string? Detail { get; init; }
}

/// <summary>
/// The link as it stands, plus the observations behind it. Both are returned
/// together because a status without its evidence is the thing operators
/// distrust.
/// </summary>
public sealed record StoreHealthResponse
{
    public required Guid StoreId { get; init; }

    public required string IntegrationStatus { get; init; }

    public DateTimeOffset? LastSeenAt { get; init; }

    public string? ApplicationVersion { get; init; }

    public StoreHealthCheckResponse? Latest { get; init; }

    public required IReadOnlyCollection<StoreHealthCheckResponse> History { get; init; }
}

public sealed record StoreDeploymentResponse
{
    public required Guid Id { get; init; }

    public required string Version { get; init; }

    public string? PreviousVersion { get; init; }

    public required DateTimeOffset DeployedAt { get; init; }

    public required DateTimeOffset DetectedAt { get; init; }

    /// <summary>VersionChange when KNIGHT noticed it, StoreReported when the store announced it.</summary>
    public required string Source { get; init; }

    /// <summary>
    /// Who deployed it. Null for every deployment in phase 3: KNIGHT learns
    /// about these from the store, which does not know who ran the pipeline.
    /// Provisioning and the agent fill it in later.
    /// </summary>
    public string? DeployedBy { get; init; }

    /// <summary>Detected, Succeeded, Failed or RolledBack.</summary>
    public required string Status { get; init; }

    public string? Notes { get; init; }
}

/// <summary>
/// What has to be published on the store's domain, and where. The token is not a
/// secret: publishing it is the proof.
/// </summary>
public sealed record DomainVerificationResponse
{
    public required Guid StoreId { get; init; }

    public required string Domain { get; init; }

    public required string Token { get; init; }

    /// <summary>The path the token must be served from over HTTP.</summary>
    public required string HttpPath { get; init; }

    /// <summary>The TXT record name carrying the token, for stores with no HTTP surface yet.</summary>
    public required string DnsRecordName { get; init; }

    public required DateTimeOffset IssuedAt { get; init; }

    public DateTimeOffset? VerifiedAt { get; init; }
}

public sealed record DomainVerificationAttemptResponse
{
    public required bool Verified { get; init; }

    public string? Method { get; init; }

    /// <summary>Why it failed, in one line an operator can act on.</summary>
    public string? Detail { get; init; }

    public DateTimeOffset? VerifiedAt { get; init; }
}

/// <summary>
/// An error a store reported, before grouping exists. Phase 5 turns these into
/// groups with counts and a lifecycle; until then the dashboard shows the raw
/// stream rather than pretending the feature is missing.
/// </summary>
public sealed record StoreErrorEventResponse
{
    public required Guid Id { get; init; }

    public required Guid StoreId { get; init; }

    public required DateTimeOffset OccurredAt { get; init; }

    public required DateTimeOffset ReceivedAt { get; init; }

    public required string Environment { get; init; }

    public string? StoreVersion { get; init; }

    public required string ExceptionType { get; init; }

    public required string Message { get; init; }

    public string? Endpoint { get; init; }

    public string? HttpMethod { get; init; }

    public int? StatusCode { get; init; }

    public string? StackTrace { get; init; }

    public string? RequestId { get; init; }

    public string? TraceId { get; init; }
}

public sealed record StoreEventResponse
{
    public required Guid Id { get; init; }

    public required Guid StoreId { get; init; }

    public required DateTimeOffset OccurredAt { get; init; }

    public required DateTimeOffset ReceivedAt { get; init; }

    public required string Type { get; init; }

    public required string Severity { get; init; }

    public required string Summary { get; init; }

    public string? TraceId { get; init; }
}

/// <summary>
/// A domain the store answers on, and whether ownership of it has been proven.
/// One row today — the primary domain — because aliases arrive with provisioning
/// in phase 9 and a list now keeps that from being a breaking change.
/// </summary>
public sealed record StoreDomainResponse
{
    public required Guid Id { get; init; }

    public required string Host { get; init; }

    /// <summary>Primary today; Alias and Staging arrive with provisioning.</summary>
    public required string Type { get; init; }

    /// <summary>NotStarted, Pending or Verified.</summary>
    public required string Verification { get; init; }

    public DateTimeOffset? VerifiedAt { get; init; }

    /// <summary>HttpToken or DnsTextRecord, once proven.</summary>
    public string? VerificationMethod { get; init; }
}

/// <summary>
/// One entry in a store's own account of its life, built from the lifecycle
/// events it reported. What an operator did to the store lives in the audit log
/// and is queried separately.
/// </summary>
public sealed record StoreActivityResponse
{
    public required Guid Id { get; init; }

    public required string Title { get; init; }

    public required string Actor { get; init; }

    public required DateTimeOffset OccurredAt { get; init; }

    /// <summary>event or warning, so the timeline can colour it.</summary>
    public required string Kind { get; init; }
}

/// <summary>
/// One structured log line a store shipped. Levels are normalised on the way out
/// — stores log in whatever vocabulary their framework uses — while the stream
/// itself is stored exactly as sent.
/// </summary>
public sealed record StoreLogEntryResponse
{
    public required Guid Id { get; init; }

    public required DateTimeOffset Timestamp { get; init; }

    /// <summary>Debug, Information, Warning, Error or Critical.</summary>
    public required string Level { get; init; }

    public required string Service { get; init; }

    public required Guid StoreId { get; init; }

    /// <summary>The store's primary domain, so a row says where it came from.</summary>
    public string? StoreName { get; init; }

    public required string Environment { get; init; }

    public required string Message { get; init; }

    public string? TraceId { get; init; }
}
