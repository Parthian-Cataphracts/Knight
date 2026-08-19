using Knight.Application.Abstractions.ControlPlane;
using Knight.Application.Abstractions.Time;
using Knight.Application.Exceptions;
using Microsoft.Extensions.Options;
using Servers.Domain;

namespace Servers;

/// <summary>
/// The agent side of monitoring.
///
/// Enrolment is the interesting part. A presented provisioning token is matched
/// against the hashes of every agent still awaiting one — the token names no
/// agent, so a provisioning script carries one secret rather than a secret and an
/// identity. The scan is over agents awaiting enrolment, which is a handful even
/// on a large fleet.
///
/// Every credential comparison here is fixed-time, and a failure says only that
/// it failed. An enrolment endpoint that let a caller distinguish "unknown token"
/// from "expired token" would be an oracle for guessing tokens.
/// </summary>
internal sealed class AgentService : IAgentService
{
    private readonly IAgentRepository _agents;
    private readonly IServerRepository _servers;
    private readonly IServerMetricRepository _metrics;
    private readonly ISecureTokenFactory _tokens;
    private readonly IAuditTrail _audit;
    private readonly IDateTimeProvider _clock;
    private readonly ServerOptions _options;

    public AgentService(
        IAgentRepository agents,
        IServerRepository servers,
        IServerMetricRepository metrics,
        ISecureTokenFactory tokens,
        IAuditTrail audit,
        IDateTimeProvider clock,
        IOptions<ServerOptions> options)
    {
        _agents = agents;
        _servers = servers;
        _metrics = metrics;
        _tokens = tokens;
        _audit = audit;
        _clock = clock;
        _options = options.Value;
    }

    public async Task<AgentEnrolmentResult?> EnrolAsync(AgentEnrolmentInput input, CancellationToken cancellationToken)
    {
        var now = _clock.UtcNow;

        if (string.IsNullOrWhiteSpace(input.ProvisioningToken))
        {
            return null;
        }

        var presented = _tokens.Hash(input.ProvisioningToken.Trim());
        var candidates = await _agents.ListAwaitingEnrolmentAsync(cancellationToken);

        Agent? matched = null;
        foreach (var candidate in candidates)
        {
            // Every candidate is compared, without an early exit, so that how long
            // this takes says nothing about how close a guess was.
            if (candidate.ProvisioningTokenHash is { } stored && FixedTimeEquals(stored, presented))
            {
                matched = candidate;
            }
        }

        if (matched is null || !matched.CanEnrol(now))
        {
            // One answer for unknown, expired and already-used. A caller learns
            // that it may not enrol, not which half of its guess was wrong.
            return null;
        }

        var credential = _tokens.Generate();
        matched.CompleteEnrolment(credential.Hash, input.Version, input.Capabilities, now);

        // A server whose agent has just enrolled is, by definition, reporting.
        var server = await _servers.GetByIdAsync(matched.ServerId, cancellationToken);
        server?.RecordHeartbeat(now);

        await _agents.SaveChangesAsync(cancellationToken);
        await _servers.SaveChangesAsync(cancellationToken);

        await _audit.RecordAsync(
            "agent.enrolled",
            "Agent",
            matched.Id.ToString(),
            null,
            cancellationToken,
            newValue: new { matched.ServerId, matched.Version });

        return new AgentEnrolmentResult(matched.Id, matched.ServerId, credential.RawValue, _options.HeartbeatInterval);
    }

    public async Task<Agent?> AuthenticateAsync(Guid agentId, string credential, CancellationToken cancellationToken)
    {
        if (agentId == Guid.Empty || string.IsNullOrWhiteSpace(credential))
        {
            return null;
        }

        var agent = await _agents.GetByIdAsync(agentId, cancellationToken);

        if (agent?.CredentialHash is null || agent.Status is AgentStatus.Revoked)
        {
            // Hash anyway. Returning early on an unknown agent is what would make
            // an unknown id measurably faster to refuse than a wrong secret.
            _ = _tokens.Hash(credential);
            return null;
        }

        return FixedTimeEquals(agent.CredentialHash, _tokens.Hash(credential)) ? agent : null;
    }

    public async Task<AgentHeartbeatResult> HeartbeatAsync(
        Guid agentId,
        AgentHeartbeatInput input,
        CancellationToken cancellationToken)
    {
        var now = _clock.UtcNow;

        var agent = await _agents.GetByIdAsync(agentId, cancellationToken)
            ?? throw new NotFoundException("The agent was not found.");

        agent.RecordHeartbeat(input.Version, input.Capabilities, now);

        var server = await _servers.GetByIdAsync(agent.ServerId, cancellationToken)
            ?? throw new NotFoundException("The agent's server no longer exists.");

        server.RecordHeartbeat(now);

        if (input.Metrics is { } sample)
        {
            await _metrics.AddAsync(
                ServerMetric.Capture(
                    Guid.CreateVersion7(),
                    server.Id,
                    now,
                    sample.CpuPercent,
                    sample.MemoryUsedBytes,
                    sample.MemoryTotalBytes,
                    sample.DiskUsedBytes,
                    sample.DiskTotalBytes,
                    sample.NetInBytes,
                    sample.NetOutBytes,
                    sample.LoadAverage),
                cancellationToken);

            // The sample can move the server off Healthy in the same breath as
            // the heartbeat that set it. Evaluating here rather than waiting for
            // the sweep means a box filling its disk is visible on the next
            // heartbeat, not up to a minute later.
            ApplyThresholds(server, sample, now);
        }

        await _agents.SaveChangesAsync(cancellationToken);
        await _servers.SaveChangesAsync(cancellationToken);
        await _metrics.SaveChangesAsync(cancellationToken);

        return new AgentHeartbeatResult(_options.HeartbeatInterval, server.Status);
    }

    /// <summary>
    /// Turns a sample into a status.
    ///
    /// Disk is checked before memory and CPU because it is the one that does not
    /// recover on its own: a full disk stays full, and a store on it stops being
    /// able to write at all.
    /// </summary>
    private void ApplyThresholds(Server server, ServerMetricSample sample, DateTimeOffset now)
    {
        var diskPercent = sample.DiskTotalBytes > 0
            ? sample.DiskUsedBytes * 100d / sample.DiskTotalBytes
            : 0;

        var memoryPercent = sample.MemoryTotalBytes > 0
            ? sample.MemoryUsedBytes * 100d / sample.MemoryTotalBytes
            : 0;

        if (diskPercent >= _options.DiskCriticalPercent)
        {
            server.ApplyStatus(
                ServerStatus.Degraded,
                $"Disk is {diskPercent:F0}% full, at or above the {_options.DiskCriticalPercent}% threshold.",
                now);
            return;
        }

        if (memoryPercent >= _options.MemoryCriticalPercent)
        {
            server.ApplyStatus(
                ServerStatus.Degraded,
                $"Memory is {memoryPercent:F0}% used, at or above the {_options.MemoryCriticalPercent}% threshold.",
                now);
            return;
        }

        if (sample.CpuPercent >= _options.CpuCriticalPercent)
        {
            server.ApplyStatus(
                ServerStatus.Degraded,
                $"CPU is at {sample.CpuPercent:F0}%, at or above the {_options.CpuCriticalPercent}% threshold.",
                now);
        }
    }

    /// <summary>
    /// Compares two hashes without leaking, through timing, how much of them
    /// matched.
    /// </summary>
    private static bool FixedTimeEquals(string left, string right)
    {
        var a = System.Text.Encoding.UTF8.GetBytes(left);
        var b = System.Text.Encoding.UTF8.GetBytes(right);

        return a.Length == b.Length && System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(a, b);
    }
}
