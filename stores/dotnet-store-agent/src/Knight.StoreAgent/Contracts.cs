using System.Text.Json.Serialization;

namespace Knight.StoreAgent;

/// <summary>What KNIGHT answers a handshake with.</summary>
public sealed record StoreIdentity
{
    public Guid StoreId { get; init; }

    public string StoreName { get; init; } = string.Empty;

    public string Slug { get; init; } = string.Empty;

    public string Environment { get; init; } = string.Empty;

    /// <summary>Pending while the domain is unproven, Connected once it is.</summary>
    public string IntegrationStatus { get; init; } = string.Empty;

    public string AccessToken { get; init; } = string.Empty;

    public string TokenType { get; init; } = string.Empty;

    public int ExpiresIn { get; init; }

    /// <summary>
    /// A replacement credential, present only when this handshake rotated one that
    /// was nearing expiry. The agent persists it and authenticates with it from the
    /// next handshake on; the credential it presented this time keeps working
    /// through its grace window, so nothing breaks in between. Null on an ordinary
    /// handshake.
    /// </summary>
    public RotatedCredential? RotatedCredential { get; init; }
}

/// <summary>The plaintext of a rotated credential, delivered once on the handshake that rotated it.</summary>
public sealed record RotatedCredential
{
    public string ClientId { get; init; } = string.Empty;

    /// <summary>Never logged. The one moment KNIGHT ever hands this back is this response.</summary>
    public string ClientSecret { get; init; } = string.Empty;

    public DateTimeOffset? ExpiresAt { get; init; }

    public bool IsComplete =>
        !string.IsNullOrWhiteSpace(ClientId) && !string.IsNullOrWhiteSpace(ClientSecret);
}

/// <summary>
/// One installation job, exactly as <c>StoreJobEndpoints</c> produces it.
///
/// The step list comes from KNIGHT rather than being decided here. A store that
/// chose its own order would be a store where "install then migrate" meant
/// something different depending on who wrote it, and the whole point of the
/// vocabulary is that it does not.
/// </summary>
public sealed record AgentJob
{
    public Guid JobId { get; init; }

    /// <summary>Install, Upgrade, Rollback, Enable, Disable, Uninstall, Configure.</summary>
    public string Type { get; init; } = string.Empty;

    public string FeatureSlug { get; init; } = string.Empty;

    public string? TargetVersion { get; init; }

    public string? CorrelationId { get; init; }

    /// <summary>
    /// <c>in_process</c> or <c>external_service</c>.
    ///
    /// It tells the agent what the artifact it is about to fetch *is*: an
    /// archive of code to unpack, or a signed configuration document to read.
    /// The agent has to know before it fetches, which is why this is on the job
    /// rather than only inside the artifact (adr/0033).
    /// </summary>
    public string Architecture { get; init; } = "in_process";

    /// <summary>Whether the store runs none of this Feature's code.</summary>
    public bool IsExternalService =>
        string.Equals(Architecture, "external_service", StringComparison.Ordinal);

    public IReadOnlyList<string> Steps { get; init; } = [];

    public AgentArtifact? Artifact { get; init; }

    public AgentConfiguration? Configuration { get; init; }

    public AgentMigrations? Migrations { get; init; }

    /// <summary>How this Feature attaches to the store, in the runtime-neutral names of adr/0032 §3.</summary>
    public AgentRuntime? Runtime { get; init; }

    public string? HealthCheck { get; init; }
}

public sealed record AgentArtifact
{
    public string PackageReference { get; init; } = string.Empty;

    /// <summary>Bare lowercase hex. The signature is over this exact ASCII string.</summary>
    public string Digest { get; init; } = string.Empty;

    public long SizeBytes { get; init; }

    public string Signature { get; init; } = string.Empty;

    public string SigningKeyId { get; init; } = string.Empty;

    public string DownloadUrl { get; init; } = string.Empty;

    public DateTimeOffset? DownloadUrlExpiresAt { get; init; }
}

public sealed record AgentConfiguration
{
    public int Version { get; init; }

    public string ValuesJson { get; init; } = "{}";

    public IReadOnlyDictionary<string, string> Secrets { get; init; } = new Dictionary<string, string>();
}

public sealed record AgentMigrations
{
    public bool Required { get; init; }

    public bool Reversible { get; init; }

    public bool RequiresMaintenanceWindow { get; init; }

    public IReadOnlyList<string> Extensions { get; init; } = [];
}

/// <summary>
/// The three neutral names, plus the workers the Feature declared.
///
/// A .NET Feature spells them assembly and mount type; the field names here are
/// KNIGHT's, not .NET's, which is the point.
/// </summary>
public sealed record AgentRuntime
{
    [JsonPropertyName("runtime")]
    public string Name { get; init; } = "dotnet";

    public string Namespace { get; init; } = string.Empty;

    /// <summary>What the store loads. For .NET, the assembly name without its extension.</summary>
    public string Module { get; init; } = string.Empty;

    /// <summary>The type that registers the Feature's endpoints.</summary>
    public string? MountExport { get; init; }

    public string? MountPrefix { get; init; }

    public IReadOnlyList<AgentWorker> Workers { get; init; } = [];
}

public sealed record AgentWorker
{
    public string Name { get; init; } = string.Empty;

    /// <summary>For .NET, <c>Namespace.Type#Method</c>.</summary>
    public string Entrypoint { get; init; } = string.Empty;

    public string? Schedule { get; init; }
}
