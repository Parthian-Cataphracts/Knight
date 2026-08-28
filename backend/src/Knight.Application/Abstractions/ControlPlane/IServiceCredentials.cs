namespace Knight.Application.Abstractions.ControlPlane;

/// <summary>
/// Where a Feature's service lives, and what the shared secret with it is called.
///
/// Read out of the signed manifest of the version the store has installed, never
/// out of a column beside it: the manifest is what the author signed, and a copy
/// of its endpoint could drift from it. The secret itself is never in there — the
/// manifest names the variable, KNIGHT issues the value
/// (<c>docs/adr/0034-a-shared-secret-has-a-lifetime.md</c>).
/// </summary>
public sealed record ServiceEndpointDescriptor(
    Guid StoreId,
    Guid FeatureId,
    string FeatureSlug,
    string StoreSlug,
    Uri BaseUrl,
    string SecretName);

/// <summary>
/// Reads that endpoint for one store's installation of one Feature.
///
/// A reader rather than a module reference: delivery must not depend on the
/// registry, and it needs four facts rather than an aggregate.
/// </summary>
public interface IServiceEndpointReader
{
    Task<ServiceEndpointDescriptor?> ForInstallationAsync(
        Guid storeId,
        Guid featureId,
        CancellationToken cancellationToken);
}

/// <summary>
/// What KNIGHT may tell a Feature's service about the stores it serves.
///
/// The port for the other side of <c>adr/0034</c>. KNIGHT signs as itself with
/// a control-plane secret that is not any store's, because a store cannot prove
/// it is a store before it has a secret and issuing that secret is what these
/// calls do.
///
/// Every call is idempotent, because KNIGHT retries one it is not sure arrived
/// and a retry that issued a second credential would be worse than the
/// uncertainty.
/// </summary>
public interface IServiceControlPlane
{
    /// <summary>
    /// Tells the service a store exists and what it may sign with.
    ///
    /// Registration and the first secret are one call because they are one fact:
    /// a store registered without a secret is registered and unable to say
    /// anything.
    /// </summary>
    Task RegisterAsync(
        ServiceEndpointDescriptor endpoint,
        string secret,
        CancellationToken cancellationToken);

    /// <summary>
    /// Issues a new secret without cutting off the old one.
    ///
    /// <paramref name="overlapSeconds"/> is how long the previous secret keeps
    /// working. It exists so a rotation is a deploy rather than an outage: the
    /// store is still signing with the old value until it takes delivery of the
    /// new configuration, and everything in flight at that moment was signed
    /// before it.
    /// </summary>
    Task RotateAsync(
        ServiceEndpointDescriptor endpoint,
        string secret,
        int overlapSeconds,
        CancellationToken cancellationToken);

    /// <summary>
    /// Stops the store reaching the service at all.
    ///
    /// The half of a withdrawn entitlement that the store cannot be trusted with:
    /// a store whose registry is stale, wrong or restored from a backup would
    /// otherwise keep calling a Feature nobody pays for.
    /// </summary>
    Task RevokeAsync(ServiceEndpointDescriptor endpoint, CancellationToken cancellationToken);
}

/// <summary>What happened to a store's credential with one Feature's service.</summary>
/// <param name="Rotated">
/// False when the store had no secret with this service before, which is an
/// issue rather than a rotation. Worth distinguishing in an audit trail: the
/// first one is a store being connected and the second is a credential being
/// replaced.
/// </param>
public sealed record ServiceCredentialResult(
    Guid StoreId,
    string FeatureSlug,
    string SecretName,
    bool Rotated,
    int OverlapSeconds,
    int ConfigurationVersion);

/// <summary>
/// Issuing, rotating and revoking the secret a store signs a Feature's service
/// with.
///
/// The secret is generated here, sent to the service, and delivered to the store
/// as a configuration secret down the path every other secret already travels.
/// The order matters and is not an implementation detail: the service is told
/// **first**, so that the store never holds a credential the service has not
/// heard of. The reverse order has a window in which a store signs with
/// something that cannot verify, and that window is an outage.
/// </summary>
public interface IServiceCredentialService
{
    Task<ServiceCredentialResult> IssueAsync(
        Guid storeId,
        Guid featureId,
        int? overlapSeconds,
        CancellationToken cancellationToken);

    Task<ServiceCredentialResult> RevokeAsync(
        Guid storeId,
        Guid featureId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Revokes every store of one customer's credential with one Feature's
    /// service. What a withdrawn entitlement acts on.
    ///
    /// Returns what it did rather than throwing when a store's Feature is not a
    /// service: a customer's entitlement covers stores that may have it
    /// installed either way, and an entitlement change must not fail because one
    /// of them runs the in-process build.
    /// </summary>
    Task<IReadOnlyCollection<ServiceCredentialResult>> RevokeForCustomerAsync(
        Guid customerId,
        Guid featureId,
        CancellationToken cancellationToken);
}
