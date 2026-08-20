using Stores.Domain;

namespace Stores;

/// <summary>
/// What a store presents to prove who it is. The nonce is optional: a store on a
/// network where it cannot be replayed gains nothing from it, and one behind a
/// proxy that retries may legitimately send the same request twice.
/// </summary>
public sealed record StoreHandshakeRequest(
    string ClientId,
    string ClientSecret,
    string Environment,
    string? StoreVersion,
    string? Runtime,
    string? Nonce);

/// <summary>
/// Everything a store needs to operate for the next half hour: the token, how to
/// verify what KNIGHT signs for it, how often to come back, and whether anything
/// is still standing between it and <see cref="IntegrationStatus.Connected"/>.
/// </summary>
public sealed record StoreHandshakeAccepted(
    Guid StoreId,
    string StoreName,
    string Slug,
    string Environment,
    IntegrationStatus IntegrationStatus,
    string AccessToken,
    DateTimeOffset ExpiresAt,
    int ExpiresInSeconds,
    string EntitlementSigningKey,
    bool DomainVerificationOutstanding,
    string? DomainVerificationToken,
    int HeartbeatSeconds,
    int FeatureRefreshSeconds);

/// <summary>
/// A handshake either produced a session or refused, and a refusal never says
/// which check failed — the endpoint answers all of them identically
/// (docs/authentication.md §2).
/// </summary>
public sealed record StoreHandshakeResult(HandshakeRefusal Refusal, StoreHandshakeAccepted? Session)
{
    public bool IsAccepted => Refusal is HandshakeRefusal.None && Session is not null;

    public static StoreHandshakeResult Refused(HandshakeRefusal refusal) => new(refusal, null);

    public static StoreHandshakeResult Accepted(StoreHandshakeAccepted session) => new(HandshakeRefusal.None, session);
}

/// <summary>What a store says about itself when it checks in.</summary>
public sealed record StoreHeartbeatInput(
    Guid StoreId,
    StoreHealthStatus Status,
    string? StoreVersion,
    string? DependenciesJson,
    string? FeaturesJson,
    string? Detail);

/// <summary>The state a contact left the link in, for the caller to report back.</summary>
public sealed record StoreContactResult(
    IntegrationStatus IntegrationStatus,
    bool DomainVerificationOutstanding,
    DateTimeOffset ObservedAt);

/// <summary>
/// What a store says about a backup it took. KNIGHT never takes the backup and
/// never holds it — it records that the store says one exists, and complains
/// when nobody says so for too long.
/// </summary>
public sealed record StoreBackupInput(
    Guid StoreId,
    BackupStatus Status,
    BackupKind Kind,
    DateTimeOffset StartedAt,
    DateTimeOffset? CompletedAt,
    long? SizeBytes,
    string? Location,
    string? Detail);

public sealed record StoreDeploymentInput(
    Guid StoreId,
    string Version,
    string? PreviousVersion,
    DateTimeOffset DeployedAt,
    StoreDeploymentStatus Status,
    string? Notes);

/// <summary>What an operator must publish on the domain, and where.</summary>
public sealed record DomainVerificationChallenge(
    Guid StoreId,
    string Domain,
    string Token,
    string HttpPath,
    string DnsRecordName,
    DateTimeOffset IssuedAt,
    DateTimeOffset? VerifiedAt);

public sealed record DomainVerificationResult(
    bool Verified,
    string? Method,
    string? Detail,
    DateTimeOffset? VerifiedAt);

/// <summary>
/// The store lifecycle that runs after registration: proving the link, keeping
/// it observed, and recording what the store reports
/// (docs/store-integration.md §2).
///
/// Deliberately separate from <see cref="IStoreManagementService"/>, which is
/// the dashboard's write path. These two have different callers, different
/// principals and different failure modes: an operator renaming a store and a
/// store proving its identity every thirty minutes have nothing in common but
/// the aggregate they touch.
/// </summary>
public interface IStoreIntegrationService
{
    Task<StoreHandshakeResult> HandshakeAsync(StoreHandshakeRequest request, CancellationToken cancellationToken);

    Task<StoreContactResult> RecordHeartbeatAsync(StoreHeartbeatInput input, CancellationToken cancellationToken);

    /// <summary>Applies what a poll saw. Used by the health poller, which has already made the call.</summary>
    Task<StoreContactResult> RecordProbeAsync(
        Guid storeId,
        StoreHealthStatus status,
        int? latencyMs,
        string? reportedVersion,
        string? dependenciesJson,
        string? featuresJson,
        string? detail,
        CancellationToken cancellationToken);

    Task<StoreDeployment> RecordDeploymentAsync(StoreDeploymentInput input, CancellationToken cancellationToken);

    /// <summary>
    /// Records a backup report. A failed report raises <c>backup.failed</c> for
    /// the store; a successful one closes it, because a backup that worked is
    /// the only thing that can honestly clear "this store's backups are broken".
    /// </summary>
    Task<StoreBackup> RecordBackupAsync(StoreBackupInput input, CancellationToken cancellationToken);

    Task<IReadOnlyCollection<StoreBackup>> ListBackupsAsync(Guid storeId, int limit, CancellationToken cancellationToken);

    Task<DomainVerificationChallenge> StartDomainVerificationAsync(Guid storeId, CancellationToken cancellationToken);

    Task<DomainVerificationChallenge?> GetDomainVerificationAsync(Guid storeId, CancellationToken cancellationToken);

    Task<DomainVerificationResult> VerifyDomainAsync(Guid storeId, CancellationToken cancellationToken);

    Task<IReadOnlyCollection<StoreHealthCheck>> ListHealthChecksAsync(Guid storeId, int limit, CancellationToken cancellationToken);

    Task<IReadOnlyCollection<StoreDeployment>> ListDeploymentsAsync(Guid storeId, int limit, CancellationToken cancellationToken);
}
