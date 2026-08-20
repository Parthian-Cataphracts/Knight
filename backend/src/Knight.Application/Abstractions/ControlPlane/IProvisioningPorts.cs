namespace Knight.Application.Abstractions.ControlPlane;

/// <summary>
/// Everything the provisioning coordinator needs to know about a store, read in
/// one go. Flattened to primitives so the provisioning module never sees a
/// <c>Store</c>: modules do not reference their siblings, and provisioning
/// touches more of them than anything else in the system.
/// </summary>
public sealed record ProvisioningStoreSnapshot(
    Guid StoreId,
    Guid CustomerId,
    string Name,
    string Slug,
    string PrimaryDomain,
    string Environment,
    string HostingModel,
    string Status,
    string IntegrationStatus,
    Guid? ServerId,
    bool IsDomainVerified,
    bool HasUsableCredential,
    bool HasHandshaked,
    bool IsHealthy);

/// <summary>
/// The store-side actions a provisioning run takes. Every one of them is a
/// decision the Stores module already owns; this is only the door into it.
/// </summary>
public interface IStoreProvisioningPort
{
    Task<ProvisioningStoreSnapshot?> GetAsync(Guid storeId, CancellationToken cancellationToken);

    /// <summary>Moves a fully provisioned store to Active. Refused by the aggregate for a store that never passed a health check.</summary>
    Task ActivateAsync(Guid storeId, CancellationToken cancellationToken);

    /// <summary>
    /// Archives the store, which revokes every credential it holds and closes
    /// the ingestion path in the same transaction — the two facts must not be
    /// separable (docs/store-integration.md §2).
    /// </summary>
    Task ArchiveAsync(Guid storeId, CancellationToken cancellationToken);
}

/// <summary>Whether the machine a store sits on has a live agent, and how to take it away again.</summary>
public interface IServerProvisioningPort
{
    Task<bool> HasEnrolledAgentAsync(Guid serverId, CancellationToken cancellationToken);

    /// <summary>
    /// Revokes the agents on a server that is being handed back. Only ever
    /// called for a machine dedicated to the customer being deprovisioned: an
    /// agent on shared hardware serves other people's stores.
    /// </summary>
    Task<int> RevokeAgentsAsync(Guid serverId, string reason, CancellationToken cancellationToken);
}

/// <summary>How far the base Feature installation has got.</summary>
public sealed record BaseFeatureProgress(int Total, int Completed, int Failed, string? Detail)
{
    public bool IsComplete => Failed == 0 && Completed >= Total;
}

/// <summary>
/// Installs the Features a newly provisioned store is entitled to, and disables
/// them again when it is deprovisioned.
///
/// Provisioning does not decide what a store should run — the plan and the
/// entitlements do. This port turns that answer into ordinary feature-delivery
/// jobs, which is the whole point of reusing the delivery pipeline rather than
/// inventing a parallel one (docs/store-provisioning.md).
/// </summary>
public interface IBaseFeatureInstaller
{
    Task<BaseFeatureProgress> EnsureBaseFeaturesAsync(Guid storeId, CancellationToken cancellationToken);

    /// <summary>Disables every Feature installed in the store. Returns how many were switched off.</summary>
    Task<int> DisableAllAsync(Guid storeId, string reason, CancellationToken cancellationToken);
}

/// <summary>What a purge deleted, so the audit entry can say more than "done".</summary>
public sealed record PurgeSummary(int ErrorEvents, int LogEntries, int LifecycleEvents, int HealthChecks, int Deployments, int Backups)
{
    public int Total => ErrorEvents + LogEntries + LifecycleEvents + HealthChecks + Deployments + Backups;
}

/// <summary>
/// Deletes everything KNIGHT holds about a store once its retention window has
/// closed. Irreversible by design: a retention promise that leaves the data
/// behind is not a retention promise (docs/store-provisioning.md §5).
/// </summary>
public interface IStoreDataPurger
{
    Task<PurgeSummary> PurgeAsync(Guid storeId, CancellationToken cancellationToken);
}

/// <summary>
/// How long a customer's data is kept after their store is deprovisioned.
///
/// The plan sets the default and a customer may have a negotiated override; the
/// override wins, because it is the thing that was actually agreed with them
/// (TODO.md phase 9, per-customer retention overrides by plan).
/// </summary>
public interface IRetentionPolicyReader
{
    Task<TimeSpan> ResolveAsync(Guid customerId, CancellationToken cancellationToken);
}

/// <summary>A published base store image, in the only terms provisioning needs.</summary>
public sealed record BaseImageDescriptor(string Version, string StoreVersion, string ArtifactDigest);

/// <summary>
/// Looks up the base store image an operator says a store instance was built
/// from. Provisioning records the answer on the run, so "which image is this
/// store on" is a question the incident can ask months later.
/// </summary>
public interface IBaseImageCatalog
{
    /// <summary>Null when no such image exists, or when it exists but has been yanked.</summary>
    Task<BaseImageDescriptor?> FindUsableAsync(string version, CancellationToken cancellationToken);
}

/// <summary>Where a machine sits, and who it belongs to if anybody.</summary>
public sealed record ServerPlacement(Guid ServerId, string HostingModel, string Environment, Guid? DedicatedCustomerId, bool IsDecommissioned);

/// <summary>
/// Reads a server's placement so a store assignment can be checked against it.
///
/// The Stores module must be able to refuse putting one customer's store on
/// another customer's dedicated machine, and it may not reference the module
/// that owns servers to find out.
/// </summary>
public interface IServerPlacementReader
{
    Task<ServerPlacement?> GetAsync(Guid serverId, CancellationToken cancellationToken);
}
