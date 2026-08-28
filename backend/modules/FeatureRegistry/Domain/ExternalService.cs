namespace FeatureRegistry.Domain;

/// <summary>
/// How a Feature reaches a store: as code that runs inside it, or as a service
/// the store talks to.
///
/// The second discriminator this manifest has grown, and it sits above the
/// first. <c>runtime</c> answers "what language is this code written for" and
/// only matters when there is code; <c>architecture</c> answers "is there code
/// at all". A Feature that is a service has no runtime, because the store never
/// loads anything.
///
/// Defaulted to <see cref="InProcess"/> for the same reason <c>runtime</c>
/// defaults to Django: every manifest written before this existed means the old
/// thing, and re-issuing sixteen of them to say so would be churn that proves
/// nothing (<c>adr/0033</c>).
/// </summary>
public enum FeatureArchitecture
{
    /// <summary>
    /// Code delivered into the store and loaded by it. The original model:
    /// a signed archive, a package on disk, migrations against the store's own
    /// database.
    /// </summary>
    InProcess = 0,

    /// <summary>
    /// A service the Feature's author runs, which the store talks to.
    ///
    /// KNIGHT delivers a signed **configuration document** rather than an
    /// archive: which events the service wants, which routes the store should
    /// forward to it, and where its screens hang in the store's interface. The
    /// store runs none of the Feature's code and its database is untouched.
    /// </summary>
    ExternalService = 1,
}

/// <summary>
/// Where the Feature's service lives and how the two ends authenticate.
///
/// This is the whole of what a store needs to reach a Feature, and it is
/// deliberately small. Everything else — retries, timeouts, what a 500 means —
/// belongs to the store's own client and not to a manifest, because those are
/// decisions about the store's reliability rather than about the Feature.
/// </summary>
public sealed record ServiceEndpoint(
    /// <summary>
    /// The service's base URL. Absolute, and https outside development.
    ///
    /// A store forwards shoppers' requests here, so a manifest naming a plain
    /// http origin is naming a place customer data would travel in the clear.
    /// </summary>
    Uri BaseUrl,

    /// <summary>
    /// How the store proves a request came from it, and how it checks that a
    /// webhook delivery came from the service.
    ///
    /// A closed list, like every other named thing in this manifest. The secret
    /// itself is never here: it is a configuration secret, delivered the way
    /// every other secret is.
    /// </summary>
    ServiceAuthentication Authentication,

    /// <summary>The path on the service that answers whether it is up. Relative to <see cref="BaseUrl"/>.</summary>
    string HealthPath,

    /// <summary>
    /// The name of the shared secret in the Feature's configuration.
    ///
    /// A name rather than a value, so that a manifest — which is public, signed,
    /// and stored in a catalogue — never carries a credential.
    /// </summary>
    string SecretName);

/// <summary>How a store and a Feature's service authenticate to each other.</summary>
public enum ServiceAuthentication
{
    /// <summary>An HMAC-SHA256 signature over the request, under the shared secret.</summary>
    HmacSha256 = 0,

    /// <summary>A bearer token the store holds. Simpler, and it does not authenticate the body.</summary>
    BearerToken = 1,
}

/// <summary>
/// One event the Feature wants to hear about.
///
/// The store's event bus is the store's own; this says which of its events to
/// forward and where. Nothing here lets a Feature subscribe to an event the
/// store does not publish — that is checked at install, not assumed, because a
/// silently ignored subscription is a Feature that appears installed and never
/// hears anything.
/// </summary>
public sealed record WebhookSubscription(
    /// <summary>A dotted event name from the store's published catalogue, e.g. <c>order.placed</c>.</summary>
    string Event,

    /// <summary>The path on the service to POST to. Relative to the service's base URL.</summary>
    string Path,

    /// <summary>
    /// Whether the store must keep trying.
    ///
    /// At-least-once means the store queues the delivery and retries, so the
    /// service must tolerate seeing the same event twice. At-most-once means the
    /// store sends it and forgets, which is right for something advisory and
    /// wrong for anything a customer is charged for.
    /// </summary>
    WebhookDelivery Delivery);

public enum WebhookDelivery
{
    /// <summary>Queued and retried. The service must be idempotent.</summary>
    AtLeastOnce = 0,

    /// <summary>Sent once, never retried.</summary>
    AtMostOnce = 1,
}

/// <summary>
/// A range of the store's URL space the store forwards to the Feature's service.
///
/// This is the replacement for a mounted urlconf, and the difference that
/// matters is who runs the code: a mount ran the Feature's code in the store's
/// process with the store's database handle, and a proxy makes an HTTP request
/// and returns what comes back.
/// </summary>
public sealed record ApiProxyRoute(
    /// <summary>The prefix under the store's own API, e.g. <c>subscriptions/</c>.</summary>
    string Prefix,

    /// <summary>The prefix on the service the request is rewritten onto.</summary>
    string Upstream,

    /// <summary>
    /// The HTTP methods the store will forward. Anything else is a 405 from the
    /// store, which never reaches the service.
    ///
    /// Listed rather than assumed, because "forward everything" on a route a
    /// shopper can reach is how a read-only Feature acquires a DELETE.
    /// </summary>
    IReadOnlyList<string> Methods,

    /// <summary>
    /// Whose identity the store attaches to the forwarded request.
    ///
    /// The store signs an assertion of *who is asking*; it never forwards the
    /// shopper's own session cookie or token. A Feature's service holding a
    /// store credential it could replay against the store is the thing this
    /// avoids.
    /// </summary>
    ProxyIdentity Identity);

/// <summary>Whose identity a proxied request carries.</summary>
public enum ProxyIdentity
{
    /// <summary>Nobody's. A public route.</summary>
    Anonymous = 0,

    /// <summary>The signed-in shopper, asserted by the store.</summary>
    Customer = 1,

    /// <summary>A member of the store's staff, asserted by the store.</summary>
    Staff = 2,
}

/// <summary>
/// Where the Feature's own screens appear in the store's interface.
///
/// A slot and a URL, not markup. The store decides what a slot looks like and
/// the Feature decides what is behind it, which is the only division that
/// survives the store restyling itself.
/// </summary>
public sealed record UiMount(
    /// <summary>A named place in the store's interface, e.g. <c>admin.sidebar</c>.</summary>
    string Slot,

    /// <summary>What the store shows as the entry point.</summary>
    string Label,

    /// <summary>Where it goes. Relative to the service's base URL.</summary>
    string Path,

    /// <summary>Whether the store frames it or sends the browser there.</summary>
    UiMountKind Kind);

public enum UiMountKind
{
    /// <summary>Framed inside the store's own chrome.</summary>
    Iframe = 0,

    /// <summary>A link out. The browser leaves the store.</summary>
    Redirect = 1,
}

/// <summary>
/// Everything an <c>external_service</c> Feature declares, in one block.
///
/// Null on an in-process Feature, and non-null on an external one — the reader
/// enforces both directions, so there is no manifest that is half of each.
/// </summary>
public sealed record ExternalServiceContract(
    ServiceEndpoint Service,
    IReadOnlyList<WebhookSubscription> Webhooks,
    IReadOnlyList<ApiProxyRoute> ApiProxies,
    IReadOnlyList<UiMount> UiMounts);
