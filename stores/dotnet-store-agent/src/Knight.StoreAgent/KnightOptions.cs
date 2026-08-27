using System.Collections.Generic;

namespace Knight.StoreAgent;

/// <summary>
/// What this store needs to know about KNIGHT.
///
/// Bound from configuration under <c>Knight</c>, which means environment
/// variables in a container and user-secrets on a developer's machine. The
/// client secret is read and exchanged for a short-lived token; it is never
/// written to disk by this library and never appears in a log line.
/// </summary>
public sealed class KnightOptions
{
    public const string SectionName = "Knight";

    /// <summary>Where the control plane is. No trailing slash is required; one is tolerated.</summary>
    public string BaseUrl { get; set; } = "http://localhost:5008";

    /// <summary>The credential this store was issued, from <c>POST /api/v1/stores/{id}/credentials</c>.</summary>
    public string ClientId { get; set; } = string.Empty;

    public string ClientSecret { get; set; } = string.Empty;

    /// <summary>Development, Staging or Production. Must match the store's registration.</summary>
    public string Environment { get; set; } = "Production";

    /// <summary>The version this store is running, used by KNIGHT to detect deployments.</summary>
    public string StoreVersion { get; set; } = "1.0.0";

    /// <summary>
    /// Where delivered Features and the registry live.
    ///
    /// Must be writable by the process and must survive a restart. A container
    /// that mounts nothing here loses every installed Feature on every deploy,
    /// and the failure looks like KNIGHT forgetting rather than like a missing
    /// volume.
    /// </summary>
    public string FeatureRoot { get; set; } = "knight-features";

    /// <summary>Scratch space for downloads. May be ephemeral.</summary>
    public string Workspace { get; set; } = "knight-workspace";

    /// <summary>
    /// The signing keys this store trusts, as key id to base64 SubjectPublicKeyInfo DER.
    ///
    /// Configuration, and never anything a job payload carries. A store that
    /// took the key from the same message as the signature would have checked
    /// only that the message agrees with itself.
    /// </summary>
    public Dictionary<string, string> SigningKeys { get; set; } = new();

    /// <summary>A ceiling, because a download with no limit is a disk with no floor.</summary>
    public long MaxArtifactBytes { get; set; } = 64L * 1024 * 1024;

    /// <summary>How often to tell KNIGHT this store is alive and what it runs.</summary>
    public TimeSpan HeartbeatInterval { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>How often to ask KNIGHT whether there is work.</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Whether the agent runs at all.
    ///
    /// <b>Off by default, deliberately.</b> This library is added to a store
    /// before that store has a credential — the reference goes in, the app is
    /// deployed, and the operator issues the credential afterwards. Defaulting
    /// to on would mean adding the package broke start-up until somebody
    /// finished a task they had not started yet.
    ///
    /// It also keeps a test host and a developer's machine quiet: a store that
    /// polled for jobs from inside its own test suite would install Features
    /// into whatever database the suite happened to point at.
    ///
    /// Turn it on when the credential is in place. The validation on the other
    /// settings only applies then, and it is strict, because a store that is on
    /// and cannot verify what it downloads should not run.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>Nothing here is on a request path, and a control plane that has gone away must never become one.</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(30);
}
