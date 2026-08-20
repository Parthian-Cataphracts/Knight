using Servers.Domain;

namespace Servers;

public sealed record RegisterServerInput(
    string Name,
    ServerHostingModel HostingModel,
    ServerEnvironment Environment,
    string? Provider,
    string? Region,
    string? IpAddress);

public sealed record UpdateServerInput(string Name, string? Provider, string? Region, string? IpAddress);

public sealed record ServerPage(IReadOnlyCollection<Server> Items, int Page, int PageSize, long TotalCount);

/// <summary>
/// A provisioning token, on its way to the response that shows it once.
///
/// The plaintext exists only here and in that response. It is never stored,
/// logged or audited — the same rule as a store credential, for the same reason:
/// it is the one secret in the system that cannot be rotated after it leaks,
/// because leaking it is indistinguishable from using it.
/// </summary>
public sealed record IssuedProvisioningToken(Guid AgentId, Guid ServerId, string Token, DateTimeOffset ExpiresAt);

/// <summary>Server and agent administration for the dashboard.</summary>
public interface IServerService
{
    Task<Server> RegisterAsync(RegisterServerInput input, CancellationToken cancellationToken);

    Task<Server?> GetAsync(Guid id, CancellationToken cancellationToken);

    Task<ServerPage> ListAsync(
        int page,
        int pageSize,
        ServerEnvironment? environment,
        ServerStatus? status,
        bool includeDecommissioned,
        CancellationToken cancellationToken);

    Task<Server> UpdateAsync(Guid id, UpdateServerInput input, CancellationToken cancellationToken);

    /// <summary>
    /// Records the customer a dedicated machine belongs to, or clears it when
    /// the machine returns to the shared pool. Passing null for a machine
    /// registered as dedicated is refused: dedicated to nobody is not a state
    /// that means anything to whoever is paying for it.
    /// </summary>
    Task<Server> DedicateAsync(Guid id, Guid? customerId, CancellationToken cancellationToken);

    Task DecommissionAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>Creates an agent record and issues its one-time provisioning token.</summary>
    Task<IssuedProvisioningToken> ProvisionAgentAsync(Guid serverId, CancellationToken cancellationToken);

    Task<IReadOnlyCollection<Agent>> ListAgentsAsync(Guid serverId, CancellationToken cancellationToken);

    /// <summary>Withdraws an agent's credential for good.</summary>
    Task RevokeAgentAsync(Guid agentId, string reason, CancellationToken cancellationToken);

    /// <summary>The most recent metric samples for a server, newest first.</summary>
    Task<IReadOnlyCollection<ServerMetric>> ListMetricsAsync(Guid serverId, int limit, CancellationToken cancellationToken);
}
