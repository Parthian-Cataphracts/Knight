namespace Stores.Domain;

/// <summary>
/// Environments are first class and never inferred: a token minted for one is
/// invalid in another (docs/architecture.md section 8).
/// </summary>
public enum StoreEnvironment
{
    Development = 0,
    Staging = 1,
    Production = 2,
}

/// <summary>
/// Where the store actually runs. Modelled separately from the plan: capability
/// must never be inferred from what the customer pays for
/// (docs/architecture.md section 6).
/// </summary>
public enum HostingModel
{
    SharedManaged = 0,
    DedicatedManaged = 1,
    CustomerManaged = 2,
}

public enum StoreStatus
{
    Provisioning = 0,
    Active = 1,
    Suspended = 2,
    Archived = 3,
}

/// <summary>Technical state of the KNIGHT-to-store link, distinct from the store's own status.</summary>
public enum IntegrationStatus
{
    NotRegistered = 0,
    Pending = 1,
    Connected = 2,
    Degraded = 3,
    Disconnected = 4,
}

/// <summary>
/// What a health observation said about the store. Kept separate from
/// <see cref="IntegrationStatus"/>: one is what a single check saw, the other is
/// the state the link has settled into.
/// </summary>
public enum StoreHealthStatus
{
    Healthy = 0,

    /// <summary>Answered, but reported a dependency in trouble.</summary>
    Degraded = 1,

    /// <summary>Answered, and said it is not serving.</summary>
    Unhealthy = 2,

    /// <summary>Did not answer at all: timeout, refused connection, or an unusable response.</summary>
    Unreachable = 3,
}

/// <summary>Where a health observation came from — KNIGHT asking, or the store telling.</summary>
public enum HealthCheckSource
{
    Poll = 0,
    Heartbeat = 1,
    Handshake = 2,
}

/// <summary>
/// How a store's ownership of its primary domain was proven. Both methods prove
/// the same thing — that whoever controls the domain also holds a token only
/// KNIGHT issued — and differ only in where the token is published
/// (docs/security-threat-model.md).
/// </summary>
public enum DomainVerificationMethod
{
    /// <summary>Served from <c>/.well-known/knight-domain-verification</c> on the domain itself.</summary>
    HttpToken = 0,

    /// <summary>Published as a TXT record on <c>_knight-verification.&lt;domain&gt;</c>.</summary>
    DnsTextRecord = 1,
}

/// <summary>How a deployment came to KNIGHT's attention.</summary>
public enum StoreDeploymentSource
{
    /// <summary>The store reported a version KNIGHT had not seen before.</summary>
    VersionChange = 0,

    /// <summary>The store sent a deployment lifecycle event.</summary>
    StoreReported = 1,
}

public enum StoreDeploymentStatus
{
    /// <summary>Observed after the fact, with no report of how it went.</summary>
    Detected = 0,
    Succeeded = 1,
    Failed = 2,
    RolledBack = 3,
}
