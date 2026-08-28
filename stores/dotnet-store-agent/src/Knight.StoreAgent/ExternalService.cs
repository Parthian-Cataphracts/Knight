using System.Text.Json;
using System.Text.Json.Serialization;

namespace Knight.StoreAgent;

/// <summary>
/// The store's half of the contract: which events it publishes and where a
/// Feature's screens may hang.
///
/// KNIGHT validates the <i>shape</i> of an event name at publish, because it
/// cannot know what any particular store emits. The store validates the
/// <i>name</i> at install, because it is the only thing that can. Without the
/// second check a Feature subscribing to <c>order.plaecd</c> installs cleanly,
/// passes its health check and never hears anything — and the person who
/// notices is the merchant, weeks later.
///
/// Replace these two sets with the store's own. They are the only part of this
/// library a host application is expected to change.
/// </summary>
public static class StoreEventCatalogue
{
    /// <summary>Business events the store emits, as <c>domain.thing_happened</c>.</summary>
    public static IReadOnlySet<string> KnownEvents { get; set; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "order.placed",
        "order.paid",
        "order.cancelled",
        "order.refunded",
        "order.fulfilled",
        "cart.abandoned",
        "customer.registered",
        "customer.updated",
        "product.created",
        "product.updated",
        "product.stock_changed",
        "subscription.renewal_due",
    };

    /// <summary>Where an external Feature's screens may appear.</summary>
    public static IReadOnlySet<string> UiSlots { get; set; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "admin.sidebar",
        "admin.order_detail",
        "admin.customer_detail",
        "admin.settings",
        "storefront.account",
    };
}

/// <summary>Where a Feature's service is, and how the two ends authenticate.</summary>
public sealed record ServiceEndpoint
{
    [JsonPropertyName("base_url")]
    public string BaseUrl { get; init; } = string.Empty;

    [JsonPropertyName("auth")]
    public string Authentication { get; init; } = "hmac-sha256";

    [JsonPropertyName("health")]
    public string HealthPath { get; init; } = "/health";

    /// <summary>
    /// The <b>name</b> of the shared secret, never its value.
    ///
    /// The manifest this came from is public, signed and kept in a catalogue, so
    /// a secret in it would be a secret in every copy of it for ever. The
    /// manifest names the variable; the operator sets it.
    /// </summary>
    [JsonPropertyName("secret")]
    public string SecretName { get; init; } = "KNIGHT_SERVICE_SECRET";
}

public sealed record WebhookSubscription
{
    [JsonPropertyName("event")]
    public string Event { get; init; } = string.Empty;

    [JsonPropertyName("path")]
    public string Path { get; init; } = string.Empty;

    /// <summary>
    /// <c>at-least-once</c> or <c>at-most-once</c>.
    ///
    /// The first is the default and means the service must be idempotent; the
    /// second is right for something advisory and wrong for anything a customer
    /// is charged for.
    /// </summary>
    [JsonPropertyName("delivery")]
    public string Delivery { get; init; } = "at-least-once";
}

public sealed record ApiProxyRoute
{
    [JsonPropertyName("prefix")]
    public string Prefix { get; init; } = string.Empty;

    [JsonPropertyName("upstream")]
    public string Upstream { get; init; } = "/";

    /// <summary>
    /// What the store will forward. Anything else is the store's own 405, which
    /// never reaches the service — a route that acquired a DELETE because
    /// nobody wrote a list is a read-only Feature that can now delete things.
    /// </summary>
    [JsonPropertyName("methods")]
    public IReadOnlyList<string> Methods { get; init; } = ["GET"];

    /// <summary><c>anonymous</c>, <c>customer</c> or <c>staff</c>.</summary>
    [JsonPropertyName("identity")]
    public string Identity { get; init; } = "anonymous";
}

public sealed record UiMount
{
    [JsonPropertyName("slot")]
    public string Slot { get; init; } = string.Empty;

    [JsonPropertyName("label")]
    public string Label { get; init; } = string.Empty;

    [JsonPropertyName("path")]
    public string Path { get; init; } = string.Empty;

    [JsonPropertyName("kind")]
    public string Kind { get; init; } = "iframe";
}

/// <summary>
/// The signed configuration document an external Feature is delivered as.
///
/// There is no archive because there is no code. What the store receives is
/// this: the events to forward, the routes to proxy and the screens to hang
/// (<c>adr/0033</c>).
/// </summary>
public sealed record ExternalContract
{
    [JsonPropertyName("architecture")]
    public string Architecture { get; init; } = "in_process";

    [JsonPropertyName("slug")]
    public string Slug { get; init; } = string.Empty;

    [JsonPropertyName("version")]
    public string Version { get; init; } = string.Empty;

    [JsonPropertyName("service")]
    public ServiceEndpoint? Service { get; init; }

    [JsonPropertyName("webhooks")]
    public IReadOnlyList<WebhookSubscription> Webhooks { get; init; } = [];

    [JsonPropertyName("api_proxies")]
    public IReadOnlyList<ApiProxyRoute> ApiProxies { get; init; } = [];

    [JsonPropertyName("ui_mounts")]
    public IReadOnlyList<UiMount> UiMounts { get; init; } = [];

    /// <summary>An absolute URL on the service, from a path the manifest declared.</summary>
    public string UrlFor(string path) => $"{BaseUrl}/{path.TrimStart('/')}";

    private string BaseUrl => (Service?.BaseUrl ?? string.Empty).TrimEnd('/');

    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Reads and checks a configuration document.
    ///
    /// Nothing here has been trusted before its digest and signature were
    /// checked — that ordering is the whole reason the configuration is signed
    /// at all. Without it a store would wire a proxy route, carrying its
    /// customers' requests, to whatever host answered the download URL.
    /// </summary>
    public static ExternalContract Read(byte[] bytes, string slug)
    {
        ExternalContract? contract;

        try
        {
            contract = JsonSerializer.Deserialize<ExternalContract>(bytes, Options);
        }
        catch (JsonException exception)
        {
            throw new StepFailedException(
                "install.unreadable_config",
                $"The configuration document could not be read: {exception.Message}");
        }

        if (contract is null)
        {
            throw new StepFailedException("install.unreadable_config", "The configuration document is empty.");
        }

        if (!string.Equals(contract.Architecture, "external_service", StringComparison.Ordinal))
        {
            // The job says one thing and the signed document says another.
            // Acting on either would be choosing which of two disagreeing
            // sources to trust, and the honest answer is neither.
            throw new StepFailedException(
                "install.wrong_architecture",
                "The job says this Feature is an external service and the signed document does not agree.");
        }

        if (string.IsNullOrWhiteSpace(contract.Service?.BaseUrl))
        {
            throw new StepFailedException("install.no_service", "The configuration names no service to talk to.");
        }

        foreach (var subscription in contract.Webhooks)
        {
            if (!StoreEventCatalogue.KnownEvents.Contains(subscription.Event))
            {
                throw new StepFailedException(
                    "install.unknown_event",
                    $"{slug} subscribes to '{subscription.Event}', which this store does not publish. " +
                    $"Known events: {string.Join(", ", StoreEventCatalogue.KnownEvents.Order(StringComparer.Ordinal))}.");
            }
        }

        foreach (var mount in contract.UiMounts)
        {
            if (!StoreEventCatalogue.UiSlots.Contains(mount.Slot))
            {
                throw new StepFailedException(
                    "install.unknown_slot",
                    $"{slug} hangs a screen in '{mount.Slot}', which this store does not offer. " +
                    $"Known slots: {string.Join(", ", StoreEventCatalogue.UiSlots.Order(StringComparer.Ordinal))}.");
            }
        }

        return contract;
    }
}
