using Knight.Application.Abstractions.ControlPlane;
using Knight.Application.Abstractions.Time;
using Knight.Application.Exceptions;
using Servers.Domain;

namespace Servers;

/// <summary>
/// Server and agent administration.
///
/// Everything here is platform work. There is no customer-scoped path into this
/// service, and that is the point: a customer may see that *their store* is
/// unhealthy, never which machine it runs on or what else runs beside it
/// (docs/authorization.md).
/// </summary>
internal sealed class ServerService : IServerService
{
    private const int MaxPageSize = 100;

    private readonly IServerRepository _servers;
    private readonly IAgentRepository _agents;
    private readonly IServerMetricRepository _metrics;
    private readonly ISecureTokenFactory _tokens;
    private readonly IAuditTrail _audit;
    private readonly IDateTimeProvider _clock;

    public ServerService(
        IServerRepository servers,
        IAgentRepository agents,
        IServerMetricRepository metrics,
        ISecureTokenFactory tokens,
        IAuditTrail audit,
        IDateTimeProvider clock)
    {
        _servers = servers;
        _agents = agents;
        _metrics = metrics;
        _tokens = tokens;
        _audit = audit;
        _clock = clock;
    }

    public async Task<Server> RegisterAsync(RegisterServerInput input, CancellationToken cancellationToken)
    {
        var server = Server.Register(
            Guid.CreateVersion7(),
            _clock.UtcNow,
            input.Name,
            input.HostingModel,
            input.Environment,
            input.Provider,
            input.Region,
            input.IpAddress);

        await _servers.AddAsync(server, cancellationToken);
        await _servers.SaveChangesAsync(cancellationToken);

        await _audit.RecordAsync(
            "server.registered",
            "Server",
            server.Id.ToString(),
            null,
            cancellationToken,
            newValue: new { server.Name, Environment = server.Environment.ToString(), HostingModel = server.HostingModel.ToString() });

        return server;
    }

    public Task<Server?> GetAsync(Guid id, CancellationToken cancellationToken) =>
        _servers.GetByIdAsync(id, cancellationToken);

    public async Task<ServerPage> ListAsync(
        int page,
        int pageSize,
        ServerEnvironment? environment,
        ServerStatus? status,
        bool includeDecommissioned,
        CancellationToken cancellationToken)
    {
        var safePage = page < 1 ? 1 : page;
        var safeSize = pageSize is < 1 or > MaxPageSize ? 25 : pageSize;

        var (items, total) = await _servers.ListAsync(
            safePage, safeSize, environment, status, includeDecommissioned, cancellationToken);

        return new ServerPage(items, safePage, safeSize, total);
    }

    public async Task<Server> UpdateAsync(Guid id, UpdateServerInput input, CancellationToken cancellationToken)
    {
        var server = await RequireAsync(id, cancellationToken);

        server.UpdateDetails(input.Name, input.Provider, input.Region, input.IpAddress, _clock.UtcNow);
        await _servers.SaveChangesAsync(cancellationToken);

        await _audit.RecordAsync("server.updated", "Server", server.Id.ToString(), null, cancellationToken);
        return server;
    }

    public async Task<Server> DedicateAsync(Guid id, Guid? customerId, CancellationToken cancellationToken)
    {
        var server = await RequireAsync(id, cancellationToken);
        var previous = server.DedicatedCustomerId;

        server.DedicateTo(customerId, _clock.UtcNow);
        await _servers.SaveChangesAsync(cancellationToken);

        await _audit.RecordAsync(
            "server.dedicated",
            "Server",
            server.Id.ToString(),
            customerId,
            cancellationToken,
            previousValue: new { dedicatedCustomerId = previous },
            newValue: new { dedicatedCustomerId = customerId });

        return server;
    }

    public async Task DecommissionAsync(Guid id, CancellationToken cancellationToken)
    {
        var server = await RequireAsync(id, cancellationToken);
        var now = _clock.UtcNow;

        server.Decommission(now);

        // Its agents go with it. An agent whose machine is gone is a credential
        // with nothing to protect, and leaving it usable would be leaving a way
        // in that nobody is watching any more.
        foreach (var agent in await _agents.ListForServerAsync(id, cancellationToken))
        {
            if (agent.Status is not AgentStatus.Revoked)
            {
                agent.Revoke("The server was decommissioned.", now);
            }
        }

        await _servers.SaveChangesAsync(cancellationToken);
        await _agents.SaveChangesAsync(cancellationToken);

        await _audit.RecordAsync("server.decommissioned", "Server", server.Id.ToString(), null, cancellationToken);
    }

    public async Task<IssuedProvisioningToken> ProvisionAgentAsync(Guid serverId, CancellationToken cancellationToken)
    {
        var server = await RequireAsync(serverId, cancellationToken);

        var secret = _tokens.Generate();
        var agent = Agent.Provision(
            Guid.CreateVersion7(),
            _clock.UtcNow,
            server.Id,
            secret.Hash);

        await _agents.AddAsync(agent, cancellationToken);
        await _agents.SaveChangesAsync(cancellationToken);

        // The audit records that a token was issued, never the token.
        await _audit.RecordAsync(
            "agent.provisioned",
            "Agent",
            agent.Id.ToString(),
            null,
            cancellationToken,
            newValue: new { ServerId = server.Id, server.Name });

        return new IssuedProvisioningToken(
            agent.Id,
            server.Id,
            secret.RawValue,
            agent.ProvisioningExpiresAt ?? _clock.UtcNow.Add(Agent.ProvisioningWindow));
    }

    public Task<IReadOnlyCollection<Agent>> ListAgentsAsync(Guid serverId, CancellationToken cancellationToken) =>
        _agents.ListForServerAsync(serverId, cancellationToken);

    public async Task RevokeAgentAsync(Guid agentId, string reason, CancellationToken cancellationToken)
    {
        var agent = await _agents.GetByIdAsync(agentId, cancellationToken)
            ?? throw new NotFoundException($"Agent '{agentId}' was not found.");

        agent.Revoke(reason, _clock.UtcNow);
        await _agents.SaveChangesAsync(cancellationToken);

        await _audit.RecordAsync(
            "agent.revoked",
            "Agent",
            agent.Id.ToString(),
            null,
            cancellationToken,
            newValue: new { agent.ServerId, Reason = reason });
    }

    public Task<IReadOnlyCollection<ServerMetric>> ListMetricsAsync(Guid serverId, int limit, CancellationToken cancellationToken) =>
        _metrics.ListRecentAsync(serverId, Math.Clamp(limit, 1, 1000), cancellationToken);

    private async Task<Server> RequireAsync(Guid id, CancellationToken cancellationToken) =>
        await _servers.GetByIdAsync(id, cancellationToken)
        ?? throw new NotFoundException($"Server '{id}' was not found.");
}
