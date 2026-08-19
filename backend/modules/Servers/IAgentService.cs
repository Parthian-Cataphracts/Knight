using Servers.Domain;

namespace Servers;

public sealed record AgentEnrolmentInput(string ProvisioningToken, string? Version, string? Capabilities);

/// <summary>
/// What an agent gets back once it has enrolled: its identity and the credential
/// it will authenticate with from now on. The credential appears here once and is
/// stored only as a hash.
/// </summary>
public sealed record AgentEnrolmentResult(
    Guid AgentId,
    Guid ServerId,
    string Credential,
    TimeSpan HeartbeatInterval);

public sealed record AgentHeartbeatInput(
    string? Version,
    string? Capabilities,
    ServerMetricSample? Metrics);

/// <summary>One sample, as the agent reports it.</summary>
public sealed record ServerMetricSample(
    double CpuPercent,
    long MemoryUsedBytes,
    long MemoryTotalBytes,
    long DiskUsedBytes,
    long DiskTotalBytes,
    long NetInBytes,
    long NetOutBytes,
    double? LoadAverage);

public sealed record AgentHeartbeatResult(TimeSpan HeartbeatInterval, ServerStatus ServerStatus);

/// <summary>
/// The agent's own surface: enrol, heartbeat, report metrics.
///
/// Deliberately narrow, and deliberately separate from the feature-delivery job
/// channel. An agent authenticating here can say how the machine is; it cannot
/// ask for work, and nothing here accepts a command. The job channel is the only
/// place an agent is told to do anything, and its vocabulary is closed
/// (docs/feature-delivery.md §15).
/// </summary>
public interface IAgentService
{
    /// <summary>
    /// Exchanges a one-time provisioning token for a lasting credential, or
    /// returns null when the token is refused.
    ///
    /// Null rather than an exception, and one null for every reason: unknown,
    /// expired and already-used are indistinguishable to the caller. An enrolment
    /// endpoint that let somebody tell those apart would be an oracle for
    /// guessing tokens.
    /// </summary>
    Task<AgentEnrolmentResult?> EnrolAsync(AgentEnrolmentInput input, CancellationToken cancellationToken);

    /// <summary>
    /// Authenticates a presented credential and returns the agent it belongs to,
    /// or null. Returning null rather than throwing keeps the caller from being
    /// able to tell "no such agent" from "wrong secret".
    /// </summary>
    Task<Agent?> AuthenticateAsync(Guid agentId, string credential, CancellationToken cancellationToken);

    Task<AgentHeartbeatResult> HeartbeatAsync(Guid agentId, AgentHeartbeatInput input, CancellationToken cancellationToken);
}
