namespace Knight.StoreAgent;

/// <summary>
/// What a store can show somebody about its connection to KNIGHT.
///
/// A snapshot rather than the live object, so a panel rendering it cannot see
/// half of one update and half of the next.
///
/// **No secret is in here.** Not the client secret, not a token, not a shared
/// secret a Feature was issued. A connection screen exists to answer "is this
/// working and when did it last work", and every one of those questions is
/// answerable without a credential — which is what makes it safe to put on a
/// screen, in a log, and in a support conversation.
/// </summary>
public sealed record KnightConnectionStatus
{
    /// <summary>Whether a credential has been entered at all.</summary>
    public bool Configured { get; init; }

    /// <summary>Whether the agent is meant to be talking to KNIGHT.</summary>
    public bool Enabled { get; init; }

    /// <summary>Whether the last thing it tried worked.</summary>
    public bool Connected { get; init; }

    public string BaseUrl { get; init; } = string.Empty;

    /// <summary>The client id. Half of a credential, and the half a support conversation needs.</summary>
    public string ClientId { get; init; } = string.Empty;

    public string StoreId { get; init; } = string.Empty;

    public string StoreName { get; init; } = string.Empty;

    public string Slug { get; init; } = string.Empty;

    public string IntegrationStatus { get; init; } = string.Empty;

    public DateTimeOffset? LastHandshakeAt { get; init; }

    public DateTimeOffset? LastHeartbeatAt { get; init; }

    public DateTimeOffset? LastJobAt { get; init; }

    public string LastJob { get; init; } = string.Empty;

    /// <summary>
    /// What went wrong last, in KNIGHT's words or the transport's.
    ///
    /// Kept after a recovery rather than cleared, with <see cref="LastErrorAt"/>
    /// beside it: "it is working now and it failed twice this morning" is the
    /// answer to the question somebody is actually asking.
    /// </summary>
    public string LastError { get; init; } = string.Empty;

    public DateTimeOffset? LastErrorAt { get; init; }

    /// <summary>The Features this store has taken delivery of.</summary>
    public IReadOnlyList<KnightInstalledFeatureStatus> Features { get; init; } = [];

    /// <summary>
    /// The UI mounts a store should actually render in its menu: the mounts of
    /// Features that are installed <b>and enabled</b>, and nothing else. Because a
    /// lapsed entitlement disables a Feature, a mount for one the customer no
    /// longer pays for is absent from this list — so a menu built from it cannot
    /// show a control the store is not entitled to, without the nav code having to
    /// know the first thing about entitlement (docs/authorization.md §5, phase 32B).
    /// The per-Feature <see cref="KnightInstalledFeatureStatus.UiMounts"/> stay the
    /// full picture for a management screen; this is the safe list for the shop.
    /// </summary>
    public IReadOnlyList<KnightUiMountStatus> VisibleUiMounts { get; init; } = [];
}

/// <summary>One Feature, as a connection screen needs it.</summary>
public sealed record KnightInstalledFeatureStatus
{
    public string Slug { get; init; } = string.Empty;

    public string Version { get; init; } = string.Empty;

    /// <summary>Installed and enabled are separate facts, and a screen must show both.</summary>
    public bool Enabled { get; init; }

    /// <summary><c>in_process</c> or <c>external_service</c>.</summary>
    public string Architecture { get; init; } = "in_process";

    /// <summary>The prefixes of this store's own URL space it forwards to the Feature's service.</summary>
    public IReadOnlyList<string> ProxyPrefixes { get; init; } = [];

    /// <summary>Where its screens hang, as slot and label.</summary>
    public IReadOnlyList<KnightUiMountStatus> UiMounts { get; init; } = [];

    /// <summary>Whether this store holds the shared secret the Feature's service needs.</summary>
    public bool HasServiceSecret { get; init; }
}

public sealed record KnightUiMountStatus
{
    /// <summary>Which Feature this mount belongs to. Empty on the per-Feature list, set on the flat visible list.</summary>
    public string Slug { get; init; } = string.Empty;

    public string Slot { get; init; } = string.Empty;

    public string Label { get; init; } = string.Empty;

    public string Path { get; init; } = string.Empty;

    public string Kind { get; init; } = "iframe";
}

/// <summary>
/// What the agent has been doing, kept for whoever asks.
///
/// A singleton the background services write and a panel reads. It is
/// deliberately not persisted: it describes this process, and a status restored
/// from disk after a restart would claim a heartbeat that this process never
/// sent.
/// </summary>
public sealed class KnightAgentStatus
{
    private readonly object _gate = new();

    private bool _connected;
    private string _storeId = string.Empty;
    private string _storeName = string.Empty;
    private string _slug = string.Empty;
    private string _integrationStatus = string.Empty;
    private DateTimeOffset? _handshakeAt;
    private DateTimeOffset? _heartbeatAt;
    private DateTimeOffset? _jobAt;
    private string _lastJob = string.Empty;
    private string _lastError = string.Empty;
    private DateTimeOffset? _lastErrorAt;

    /// <summary>
    /// The store's id in KNIGHT, or empty before the first handshake.
    ///
    /// It is not something a store can know about itself: KNIGHT issues it, and
    /// the handshake is where it is learned. Anything that has to *name* this
    /// store to somebody else — the proxy signing a forwarded request — has to
    /// wait for that.
    /// </summary>
    public string StoreId
    {
        get
        {
            lock (_gate)
            {
                return _storeId;
            }
        }
    }

    public void RecordHandshake(StoreIdentity identity)
    {
        lock (_gate)
        {
            _connected = true;
            _storeId = identity.StoreId.ToString();
            _storeName = identity.StoreName ?? string.Empty;
            _slug = identity.Slug ?? string.Empty;
            _integrationStatus = identity.IntegrationStatus ?? string.Empty;
            _handshakeAt = DateTimeOffset.UtcNow;
        }
    }

    public void RecordHeartbeat()
    {
        lock (_gate)
        {
            _connected = true;
            _heartbeatAt = DateTimeOffset.UtcNow;
        }
    }

    public void RecordJob(string summary)
    {
        lock (_gate)
        {
            _jobAt = DateTimeOffset.UtcNow;
            _lastJob = summary;
        }
    }

    /// <summary>
    /// Records a failure, and stops claiming the store is connected.
    ///
    /// The message is somebody else's string and is truncated on the way in. It
    /// reaches a merchant's screen, which is exactly where an unbounded remote
    /// string should not arrive.
    /// </summary>
    public void RecordFailure(string message)
    {
        lock (_gate)
        {
            _connected = false;
            _lastError = message.Length > 300 ? message[..300] : message;
            _lastErrorAt = DateTimeOffset.UtcNow;
        }
    }

    public KnightConnectionStatus Snapshot(KnightCredential credential)
    {
        lock (_gate)
        {
            return new KnightConnectionStatus
            {
                Configured = credential.IsComplete,
                Enabled = credential.Enabled,
                Connected = _connected,
                BaseUrl = credential.BaseUrl,
                ClientId = credential.ClientId,
                StoreId = _storeId,
                StoreName = _storeName,
                Slug = _slug,
                IntegrationStatus = _integrationStatus,
                LastHandshakeAt = _handshakeAt,
                LastHeartbeatAt = _heartbeatAt,
                LastJobAt = _jobAt,
                LastJob = _lastJob,
                LastError = _lastError,
                LastErrorAt = _lastErrorAt,
            };
        }
    }
}

/// <summary>
/// The whole picture, assembled: the connection, plus what has been delivered.
///
/// One call, because a screen that had to make two would show a store connected
/// to nothing for as long as the second was in flight.
/// </summary>
public sealed class KnightStatusReader(
    KnightConnection connection,
    KnightAgentStatus status,
    FeatureRegistryAccessor registry,
    Microsoft.Extensions.Options.IOptions<KnightOptions> options)
{
    private readonly KnightOptions _options = options.Value;

    public async Task<KnightConnectionStatus> ReadAsync(CancellationToken cancellationToken = default)
    {
        var credential = await connection.CurrentAsync(cancellationToken);
        var snapshot = status.Snapshot(credential);
        var installed = await registry.AllAsync(cancellationToken);

        var features = installed.Values
            .OrderBy(feature => feature.Slug, StringComparer.Ordinal)
            .Select(feature => new KnightInstalledFeatureStatus
            {
                Slug = feature.Slug,
                Version = feature.Version,
                Enabled = feature.Enabled,
                Architecture = feature.IsExternalService ? "external_service" : "in_process",
                ProxyPrefixes = feature.Contract is null
                    ? []
                    : [.. feature.Contract.ApiProxies.Select(route => route.Prefix)],
                UiMounts = feature.Contract is null
                    ? []
                    : [.. feature.Contract.UiMounts.Select(mount => new KnightUiMountStatus
                    {
                        Slot = mount.Slot,
                        Label = mount.Label,
                        Path = mount.Path,
                        Kind = mount.Kind,
                    })],
                HasServiceSecret = HasSecret(feature),
            })
            .ToList();

        // The menu a shop should draw: mounts of enabled Features only. Enabled
        // reflects entitlement, so a Feature the customer stopped paying for
        // contributes nothing here — the nav is safe to render straight from this.
        var visibleUiMounts = installed.Values
            .Where(feature => feature.Enabled && feature.Contract is not null)
            .OrderBy(feature => feature.Slug, StringComparer.Ordinal)
            .SelectMany(feature => feature.Contract!.UiMounts.Select(mount => new KnightUiMountStatus
            {
                Slug = feature.Slug,
                Slot = mount.Slot,
                Label = mount.Label,
                Path = mount.Path,
                Kind = mount.Kind,
            }))
            .ToList();

        return snapshot with { Features = features, VisibleUiMounts = visibleUiMounts };
    }

    /// <summary>
    /// Whether the shared secret this Feature's service needs has arrived.
    ///
    /// By presence, never by value. A Feature whose configuration has not
    /// reached the store yet looks identical from the outside to one that is
    /// working, right up until the first request it signs is refused — and this
    /// is the line on the screen that tells the two apart.
    /// </summary>
    private bool HasSecret(InstalledFeature feature)
    {
        if (feature.Contract?.Service is null)
        {
            return false;
        }

        return !string.IsNullOrEmpty(
            FeatureConfigurationFile.SecretFor(_options.FeatureRoot, feature.Slug, feature.Contract.Service.SecretName));
    }
}
