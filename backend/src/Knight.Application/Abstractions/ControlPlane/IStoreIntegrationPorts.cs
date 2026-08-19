namespace Knight.Application.Abstractions.ControlPlane;

/// <summary>
/// Whether a customer is in a state that lets their stores talk to KNIGHT at
/// all. A port rather than a module reference: the module that owns stores and
/// the module that owns customers stay independent of each other, exactly as
/// they do for hosting and plans.
/// </summary>
public interface ICustomerStatusReader
{
    /// <summary>
    /// False for a suspended or archived customer, and for one that does not
    /// exist. Commercial suspension has to reach ingestion immediately — a store
    /// whose customer stopped paying keeps serving its own shoppers, but it stops
    /// being managed.
    /// </summary>
    Task<bool> IsOperableAsync(Guid customerId, CancellationToken cancellationToken);
}

/// <summary>
/// One capability a customer currently holds, flattened to what a store needs to
/// enforce it: the slug its code asks about, and when the grant runs out.
/// </summary>
public sealed record EntitledFeature(
    Guid FeatureId,
    string Slug,
    string Name,
    string Source,
    DateTimeOffset GrantedAt,
    DateTimeOffset? ExpiresAt);

/// <summary>
/// Reads the entitlement set from the outside of the module that maintains it.
///
/// Ingestion needs this for two different jobs — answering a store's pull, and
/// gating the paid ingestion surfaces — and neither is a reason for the
/// ingestion module to depend on the subscriptions module. Resolution and
/// reconciliation stay where they are; this only reads the result.
/// </summary>
public interface ICustomerEntitlementReader
{
    Task<IReadOnlyCollection<EntitledFeature>> ListActiveAsync(Guid customerId, CancellationToken cancellationToken);

    /// <summary>Whether one named capability is currently held. The slug is the stable name; ids are not stable across environments.</summary>
    Task<bool> IsEntitledAsync(Guid customerId, string featureSlug, CancellationToken cancellationToken);
}

/// <summary>A minted store token and the moment it stops being accepted.</summary>
public sealed record IssuedStoreToken(string Token, DateTimeOffset ExpiresAt, TimeSpan Lifetime);

/// <summary>
/// Mints the short-lived token a store uses for every ingestion call after the
/// handshake ([`adr/0012`](../../../../docs/adr/0012-store-authentication-mechanism.md)).
///
/// The token is bound to one store, one customer and one environment, and it
/// carries <c>principal_type=store</c> so the dashboard policies reject it before
/// any handler runs (docs/authentication.md §4).
/// </summary>
public interface IStoreTokenIssuer
{
    IssuedStoreToken Issue(Guid storeId, Guid customerId, string environment, string clientId);
}

/// <summary>
/// One-shot use of a value: returns true the first time a nonce is seen inside
/// the window, false every time after. Backs replay protection on the handshake
/// and idempotency on ingestion.
///
/// The implementation is Redis where a Redis connection is configured and
/// in-process where none is, which is single-node only — see
/// [`adr/0020`](../../../../docs/adr/0020-store-ingestion-authentication.md).
/// </summary>
public interface IReplayGuard
{
    /// <summary>
    /// Atomically records <paramref name="value"/> under <paramref name="scope"/>
    /// if it is not already there. True means "first time"; false means a replay.
    /// </summary>
    Task<bool> TryConsumeAsync(string scope, string value, TimeSpan window, CancellationToken cancellationToken);
}

/// <summary>What a store answered when KNIGHT asked it how it was.</summary>
public sealed record StoreProbeResult(
    string Status,
    int? LatencyMs,
    string? ReportedVersion,
    string? ReportedEnvironment,
    string? DependenciesJson,
    string? FeaturesJson,
    string? Detail);

/// <summary>
/// Calls a store's <c>/api/knight/health</c>. Outbound, so every call is subject
/// to the egress rules in <see cref="IOutboundAddressPolicy"/>: KNIGHT resolves
/// and checks the address before it connects, because a store's domain is
/// operator-supplied data and pointing it at an internal address would turn the
/// poller into a request forger (docs/security-threat-model.md).
///
/// The request is signed with the store's derived key, so the store can refuse
/// to describe itself to anyone but KNIGHT. A health payload names versions,
/// dependencies and installed features — useful to an operator, and just as
/// useful to somebody deciding what to attack (docs/store-integration.md §5).
/// </summary>
public interface IStoreHealthProbe
{
    Task<StoreProbeResult> ProbeAsync(Guid storeId, string domain, string environment, CancellationToken cancellationToken);
}

/// <summary>Where a verification token was looked for, and whether it was there.</summary>
public sealed record DomainVerificationAttempt(bool Verified, string Method, string? Detail);

/// <summary>
/// Proves that whoever controls a store's primary domain also holds the token
/// KNIGHT issued for it, by looking for that token published on the domain.
/// </summary>
public interface IDomainOwnershipVerifier
{
    Task<DomainVerificationAttempt> VerifyAsync(string domain, string token, CancellationToken cancellationToken);
}

/// <summary>
/// Signs payloads a store must be able to trust after KNIGHT is unreachable —
/// the entitlement set above all, which a store caches and keeps using while it
/// cannot refresh (docs/store-integration.md §3).
///
/// The key is derived per store from a master key held only by KNIGHT, and the
/// derived key is handed to the store in the handshake response over TLS. A
/// cached entitlement set therefore cannot be forged by anything that did not
/// complete a handshake as that store.
/// </summary>
public interface IStorePayloadSigner
{
    /// <summary>The base64 key a store uses to verify what KNIGHT signed for it.</summary>
    string DeriveVerificationKey(Guid storeId, string environment);

    /// <summary>Base64 HMAC-SHA256 over <paramref name="canonicalPayload"/> with that store's key.</summary>
    string Sign(Guid storeId, string environment, string canonicalPayload);
}

/// <summary>
/// Decides whether KNIGHT may open an outbound connection to a resolved address.
///
/// Store domains are typed in by operators and resolved by DNS neither KNIGHT
/// nor the operator controls, so "example.test" resolving to 169.254.169.254 is
/// a normal Tuesday for an attacker and must be refused at the socket, not by
/// inspecting the hostname (docs/security-threat-model.md, SSRF).
/// </summary>
public interface IOutboundAddressPolicy
{
    /// <summary>Null when the address is allowed; otherwise the reason it was refused.</summary>
    string? Refuse(System.Net.IPAddress address);
}
