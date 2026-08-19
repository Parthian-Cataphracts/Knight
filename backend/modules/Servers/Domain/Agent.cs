using Knight.Domain.Common;
using Knight.Domain.Exceptions;

namespace Servers.Domain;

/// <summary>
/// The KNIGHT agent running on a server (docs/domain-model.md §7).
///
/// The agent is the highest-value target in the system: it runs on customer
/// infrastructure and installs code (risks.md R22). Three properties in this
/// aggregate exist because of that, and none of them is negotiable.
///
/// **A provisioning token works once.** It is issued when a server is prepared,
/// used by the agent to enrol, and dead the moment it succeeds. A token that
/// stayed valid would be a permanent credential sitting in whatever provisioning
/// script wrote it.
///
/// **Secrets are stored hashed.** KNIGHT can verify what an agent presents and
/// cannot reproduce it, so a leak of this table does not let anyone impersonate an
/// agent.
///
/// **An agent is bound to one server.** Its identity is not "an agent" but "the
/// agent on this machine", so a stolen credential reaches exactly one box.
/// </summary>
public sealed class Agent : AuditableEntity
{
    /// <summary>How long an unused provisioning token stays usable.</summary>
    public static readonly TimeSpan ProvisioningWindow = TimeSpan.FromHours(24);

    public Guid ServerId { get; private set; }

    /// <summary>The version the agent reported. Null until it first checks in.</summary>
    public string? Version { get; private set; }

    /// <summary>
    /// Hash of the one-time provisioning token. Cleared once enrolment consumes
    /// it, so a used token cannot even be replayed against the hash.
    /// </summary>
    public string? ProvisioningTokenHash { get; private set; }

    public DateTimeOffset? ProvisioningExpiresAt { get; private set; }

    /// <summary>Hash of the long-lived credential the agent authenticates with after enrolling.</summary>
    public string? CredentialHash { get; private set; }

    public AgentStatus Status { get; private set; }

    public DateTimeOffset? LastHeartbeatAt { get; private set; }

    public DateTimeOffset? EnrolledAt { get; private set; }

    public DateTimeOffset? RevokedAt { get; private set; }

    public string? RevokedReason { get; private set; }

    /// <summary>What this agent says it can do, as a document. Read, never trusted to grant anything.</summary>
    public string? Capabilities { get; private set; }

    private Agent()
    {
    }

    private Agent(Guid id, DateTimeOffset createdAt, Guid serverId, string provisioningTokenHash)
        : base(id, createdAt)
    {
        if (serverId == Guid.Empty)
        {
            throw DomainException.Validation("An agent must belong to a server.");
        }

        ServerId = serverId;
        ProvisioningTokenHash = provisioningTokenHash;
        ProvisioningExpiresAt = createdAt.Add(ProvisioningWindow);
        Status = AgentStatus.Provisioning;
    }

    /// <summary>
    /// Creates an agent record and arms its one-time provisioning token.
    ///
    /// The plaintext token is returned to the caller once and never stored; only
    /// its hash lives here.
    /// </summary>
    public static Agent Provision(Guid id, DateTimeOffset createdAt, Guid serverId, string provisioningTokenHash)
    {
        if (string.IsNullOrWhiteSpace(provisioningTokenHash))
        {
            throw DomainException.Validation("A provisioning token hash is required.");
        }

        return new Agent(id, createdAt, serverId, provisioningTokenHash);
    }

    /// <summary>
    /// Exchanges the provisioning token for a lasting credential.
    ///
    /// Deliberately not idempotent. Enrolling twice with the same token is either
    /// a replay or a second machine using a token meant for the first, and both
    /// are things this must refuse rather than quietly allow.
    /// </summary>
    public void CompleteEnrolment(string credentialHash, string? version, string? capabilities, DateTimeOffset now)
    {
        if (Status is not AgentStatus.Provisioning)
        {
            throw DomainException.Conflict($"An agent in status '{Status}' has already enrolled.");
        }

        if (ProvisioningExpiresAt is not null && now > ProvisioningExpiresAt)
        {
            throw DomainException.Conflict("The provisioning token has expired. Issue a new one.");
        }

        if (string.IsNullOrWhiteSpace(credentialHash))
        {
            throw DomainException.Validation("A credential hash is required.");
        }

        CredentialHash = credentialHash;

        // The provisioning token is burned here, not merely marked used.
        ProvisioningTokenHash = null;
        ProvisioningExpiresAt = null;

        Version = Trim(version, 50);
        Capabilities = capabilities;
        Status = AgentStatus.Online;
        EnrolledAt = now;
        LastHeartbeatAt = now;
        MarkUpdated(now);
    }

    public void RecordHeartbeat(string? version, string? capabilities, DateTimeOffset now)
    {
        if (Status is AgentStatus.Revoked)
        {
            throw DomainException.Conflict("A revoked agent cannot report.");
        }

        if (Status is AgentStatus.Provisioning)
        {
            throw DomainException.Conflict("The agent has not enrolled yet.");
        }

        LastHeartbeatAt = now;
        Status = AgentStatus.Online;

        if (!string.IsNullOrWhiteSpace(version))
        {
            Version = Trim(version, 50);
        }

        if (capabilities is not null)
        {
            Capabilities = capabilities;
        }

        MarkUpdated(now);
    }

    /// <summary>Marks the agent as not reporting. Reversible: the next heartbeat brings it back.</summary>
    public void MarkOffline(DateTimeOffset now)
    {
        if (Status is AgentStatus.Revoked or AgentStatus.Provisioning or AgentStatus.Offline)
        {
            return;
        }

        Status = AgentStatus.Offline;
        MarkUpdated(now);
    }

    /// <summary>
    /// Withdraws the agent's credential for good.
    ///
    /// Terminal, and the credential hash goes with it. Recovering a revoked agent
    /// means provisioning a new one, which is the point: revocation is what an
    /// operator reaches for when they believe the machine is compromised.
    /// </summary>
    public void Revoke(string reason, DateTimeOffset now)
    {
        if (Status is AgentStatus.Revoked)
        {
            throw DomainException.Conflict("The agent is already revoked.");
        }

        if (string.IsNullOrWhiteSpace(reason))
        {
            throw DomainException.Validation("A revocation must say why.");
        }

        Status = AgentStatus.Revoked;
        CredentialHash = null;
        ProvisioningTokenHash = null;
        ProvisioningExpiresAt = null;
        RevokedAt = now;
        RevokedReason = reason.Trim();
        MarkUpdated(now);
    }

    /// <summary>True when the agent may authenticate and act.</summary>
    public bool IsUsable => Status is AgentStatus.Online or AgentStatus.Offline && CredentialHash is not null;

    /// <summary>True when the provisioning token may still be presented.</summary>
    public bool CanEnrol(DateTimeOffset now) =>
        Status is AgentStatus.Provisioning
        && ProvisioningTokenHash is not null
        && (ProvisioningExpiresAt is null || now <= ProvisioningExpiresAt);

    public bool IsOverdue(DateTimeOffset now, TimeSpan heartbeatInterval, int missedIntervals = 3) =>
        Status is AgentStatus.Online
        && LastHeartbeatAt is not null
        && now - LastHeartbeatAt.Value > heartbeatInterval * missedIntervals;

    private static string? Trim(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        return trimmed.Length <= maxLength ? trimmed : trimmed[..maxLength];
    }
}

public enum AgentStatus
{
    /// <summary>Created, token issued, not yet enrolled.</summary>
    Provisioning = 0,

    Online = 1,

    /// <summary>Enrolled but not reporting. Comes back on its own.</summary>
    Offline = 2,

    /// <summary>Credential withdrawn. Terminal.</summary>
    Revoked = 3,
}
