namespace Knight.Contracts.Ingest;

/// <summary>
/// What a store presents to prove its identity (docs/api-contracts.md §3).
/// The secret exists only in this request; it is never returned by anything.
/// </summary>
public sealed record StoreHandshakeRequestBody
{
    public required string ClientId { get; init; }

    public required string ClientSecret { get; init; }

    /// <summary>Development, Staging or Production. Must match the store's registration.</summary>
    public required string Environment { get; init; }

    /// <summary>The version the store is running, used to detect deployments.</summary>
    public string? StoreVersion { get; init; }

    /// <summary>Free text such as "Python 3.12.9 / Django 5.1"; recorded, never parsed.</summary>
    public string? Runtime { get; init; }

    /// <summary>
    /// A value used once. Optional, and refused on a second use inside the
    /// nonce window, so a captured handshake body cannot be replayed.
    /// </summary>
    public string? Nonce { get; init; }
}

public sealed record StoreHandshakeResponse
{
    public required Guid StoreId { get; init; }

    public required string StoreName { get; init; }

    public required string Slug { get; init; }

    public required string Environment { get; init; }

    /// <summary>Pending while the domain is unproven, Connected once it is.</summary>
    public required string IntegrationStatus { get; init; }

    public required string AccessToken { get; init; }

    public required string TokenType { get; init; }

    public required int ExpiresIn { get; init; }

    public required DateTimeOffset ExpiresAt { get; init; }

    /// <summary>Base64 HMAC key for verifying payloads KNIGHT signs for this store, entitlements above all.</summary>
    public required string EntitlementSigningKey { get; init; }

    public required bool DomainVerificationOutstanding { get; init; }

    /// <summary>The token to publish on the domain; present only while verification is outstanding.</summary>
    public string? DomainVerificationToken { get; init; }

    /// <summary>Where to publish it.</summary>
    public string? DomainVerificationPath { get; init; }

    public required int HeartbeatSeconds { get; init; }

    public required int FeatureRefreshSeconds { get; init; }
}

public sealed record StoreHeartbeatRequest
{
    public required string Environment { get; init; }

    /// <summary>healthy, degraded or unhealthy — what the store says about itself.</summary>
    public required string Status { get; init; }

    public string? StoreVersion { get; init; }

    /// <summary>The store's dependency block, recorded as sent.</summary>
    public Dictionary<string, object>? Dependencies { get; init; }

    /// <summary>Feature slugs the store has installed, so entitlement and installation can be compared.</summary>
    public string[]? Features { get; init; }

    public string? Detail { get; init; }
}

public sealed record StoreHeartbeatResponse
{
    public required string IntegrationStatus { get; init; }

    public required bool DomainVerificationOutstanding { get; init; }

    public required DateTimeOffset ObservedAt { get; init; }

    public required int HeartbeatSeconds { get; init; }
}

public sealed record ErrorEventBody
{
    public required DateTimeOffset OccurredAt { get; init; }

    public required string ExceptionType { get; init; }

    public required string Message { get; init; }

    public string? Endpoint { get; init; }

    public string? HttpMethod { get; init; }

    public int? StatusCode { get; init; }

    public string? StackTrace { get; init; }

    public string? RequestId { get; init; }

    public string? TraceId { get; init; }

    /// <summary>Scrubbed store-side before it is sent; never a full request body.</summary>
    public Dictionary<string, object>? Context { get; init; }
}

public sealed record ErrorIngestRequest
{
    public required string Environment { get; init; }

    public string? Version { get; init; }

    public required ErrorEventBody[] Events { get; init; }
}

public sealed record StoreEventBody
{
    public required DateTimeOffset OccurredAt { get; init; }

    /// <summary>A dotted type such as "deployment.completed". Unknown types are stored, not refused.</summary>
    public required string Type { get; init; }

    public string? Severity { get; init; }

    public string? Summary { get; init; }

    public string? TraceId { get; init; }

    public Dictionary<string, object>? Payload { get; init; }
}

public sealed record EventIngestRequest
{
    public required string Environment { get; init; }

    public required StoreEventBody[] Events { get; init; }
}

public sealed record LogEntryBody
{
    public required DateTimeOffset Timestamp { get; init; }

    public required string Level { get; init; }

    public required string Message { get; init; }

    public string? Service { get; init; }

    public string? RequestId { get; init; }

    public string? TraceId { get; init; }

    public string? Exception { get; init; }

    public Dictionary<string, object>? Attributes { get; init; }
}

public sealed record LogIngestRequest
{
    public required string Environment { get; init; }

    public string? Version { get; init; }

    public required LogEntryBody[] Entries { get; init; }
}

/// <summary>
/// What a batch did. <c>duplicate</c> is true when the batch was recognised by
/// its idempotency key: nothing was written a second time, and the store should
/// treat it as delivered.
/// </summary>
public sealed record IngestReceiptResponse
{
    public required int Accepted { get; init; }

    public required int Rejected { get; init; }

    public required bool Duplicate { get; init; }

    /// <summary>One line per rejected item, so a store can fix what it is sending.</summary>
    public required string[] Errors { get; init; }
}

/// <summary>
/// The entitlement set as the store must enforce it, plus the signature over it.
/// A store caches this and keeps using it while KNIGHT is unreachable, so it has
/// to be verifiable offline (docs/store-integration.md §3).
/// </summary>
public sealed record EntitlementSetResponse
{
    public required Guid StoreId { get; init; }

    public required Guid CustomerId { get; init; }

    public required string Environment { get; init; }

    public required DateTimeOffset IssuedAt { get; init; }

    /// <summary>After this, the cached set is stale and must not be treated as current.</summary>
    public required DateTimeOffset StaleAfter { get; init; }

    public required EntitledFeatureResponse[] Features { get; init; }

    /// <summary>Base64 HMAC-SHA256 over the canonical form of this payload.</summary>
    public required string Signature { get; init; }

    /// <summary>Which canonicalisation the signature was computed over, so the rule can change without breaking old caches.</summary>
    public required string SignatureVersion { get; init; }
}

public sealed record EntitledFeatureResponse
{
    public required Guid FeatureId { get; init; }

    public required string Slug { get; init; }

    public required string Name { get; init; }

    /// <summary>Plan, Optional or Grant.</summary>
    public required string Source { get; init; }

    public required DateTimeOffset GrantedAt { get; init; }

    public DateTimeOffset? ExpiresAt { get; init; }
}
