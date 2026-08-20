namespace Servers.Domain;

/// <summary>
/// Persistence for servers. Platform-owned, so nothing here is customer
/// filtered: a machine is infrastructure, and a customer never sees one.
/// </summary>
public interface IServerRepository
{
    Task<Server?> GetByIdAsync(Guid id, CancellationToken cancellationToken);

    Task<(IReadOnlyCollection<Server> Items, long TotalCount)> ListAsync(
        int page,
        int pageSize,
        ServerEnvironment? environment,
        ServerStatus? status,
        bool includeDecommissioned,
        CancellationToken cancellationToken);

    /// <summary>Active servers that have reported at least once — the input to the status sweep.</summary>
    Task<IReadOnlyCollection<Server>> ListReportingAsync(CancellationToken cancellationToken);

    Task AddAsync(Server server, CancellationToken cancellationToken);

    Task SaveChangesAsync(CancellationToken cancellationToken);
}

public interface IAgentRepository
{
    Task<Agent?> GetByIdAsync(Guid id, CancellationToken cancellationToken);

    Task<IReadOnlyCollection<Agent>> ListForServerAsync(Guid serverId, CancellationToken cancellationToken);

    /// <summary>
    /// Agents for many servers at once, grouped by server id.
    ///
    /// Exists because the monitoring overview needs every server's agents and
    /// asking per server made the page cost one query per server. The fleet is
    /// the thing that grows here, so that cost grew with exactly the number the
    /// page exists to display.
    /// </summary>
    Task<IReadOnlyDictionary<Guid, IReadOnlyCollection<Agent>>> ListForServersAsync(
        IReadOnlyCollection<Guid> serverIds,
        CancellationToken cancellationToken);

    /// <summary>
    /// Every agent still awaiting enrolment, so a presented provisioning token
    /// can be matched against their hashes.
    ///
    /// The token names no agent, deliberately: a provisioning script that had to
    /// know an agent id would be a script carrying two secrets instead of one.
    /// The set is small — agents awaiting enrolment, not all agents — so scanning
    /// it is cheaper than the indirection would be.
    /// </summary>
    Task<IReadOnlyCollection<Agent>> ListAwaitingEnrolmentAsync(CancellationToken cancellationToken);

    /// <summary>Enrolled agents, for the credential lookup and the offline sweep.</summary>
    Task<IReadOnlyCollection<Agent>> ListEnrolledAsync(CancellationToken cancellationToken);

    Task AddAsync(Agent agent, CancellationToken cancellationToken);

    Task SaveChangesAsync(CancellationToken cancellationToken);
}

public interface IServerMetricRepository
{
    Task AddAsync(ServerMetric metric, CancellationToken cancellationToken);

    Task AddRangeAsync(IReadOnlyCollection<ServerMetric> metrics, CancellationToken cancellationToken);

    /// <summary>The most recent samples for one server, newest first.</summary>
    Task<IReadOnlyCollection<ServerMetric>> ListRecentAsync(Guid serverId, int limit, CancellationToken cancellationToken);

    Task<ServerMetric?> GetLatestAsync(Guid serverId, CancellationToken cancellationToken);

    /// <summary>
    /// The most recent sample for each of many servers, in one query.
    ///
    /// Same reason as <see cref="IAgentRepository.ListForServersAsync"/>: the
    /// overview wants the latest sample per server, and asking one server at a
    /// time turned the largest table in the schema into N round trips.
    /// </summary>
    Task<IReadOnlyDictionary<Guid, ServerMetric>> GetLatestForAsync(
        IReadOnlyCollection<Guid> serverIds,
        CancellationToken cancellationToken);

    /// <summary>
    /// Deletes samples older than the cutoff and answers how many went.
    ///
    /// A set-based delete rather than loading and removing: this table is the
    /// largest in the schema, and materialising a month of samples to delete them
    /// would be its own outage.
    /// </summary>
    Task<int> DeleteOlderThanAsync(DateTimeOffset cutoff, CancellationToken cancellationToken);

    Task SaveChangesAsync(CancellationToken cancellationToken);
}

public interface IAlertRepository
{
    Task<Alert?> GetByIdAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>
    /// The open alert for this rule and source, if there is one. What makes
    /// alerting deduplicate rather than accumulate.
    /// </summary>
    Task<Alert?> FindOpenAsync(string ruleKey, Guid sourceId, CancellationToken cancellationToken);

    Task<(IReadOnlyCollection<Alert> Items, long TotalCount)> ListAsync(
        int page,
        int pageSize,
        AlertSeverity? severity,
        AlertSource? source,
        bool openOnly,
        CancellationToken cancellationToken);

    Task<IReadOnlyCollection<Alert>> ListOpenForRuleAsync(string ruleKey, CancellationToken cancellationToken);

    Task AddAsync(Alert alert, CancellationToken cancellationToken);

    Task SaveChangesAsync(CancellationToken cancellationToken);
}
