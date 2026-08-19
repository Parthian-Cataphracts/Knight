using Microsoft.EntityFrameworkCore;
using Servers.Domain;

namespace Knight.Infrastructure.ControlPlane.Repositories;

/// <summary>
/// Persistence for infrastructure and monitoring.
///
/// Nothing here filters by customer: servers, agents and metrics are platform
/// facts. The alert repository is the one place a customer id appears at all, and
/// even there it is a label for routing rather than an isolation boundary — the
/// alert endpoints are platform-only.
/// </summary>
internal sealed class ServerRepository : IServerRepository
{
    private readonly ControlPlaneDbContext _context;

    public ServerRepository(ControlPlaneDbContext context)
    {
        _context = context;
    }

    public Task<Server?> GetByIdAsync(Guid id, CancellationToken cancellationToken) =>
        _context.Servers.FirstOrDefaultAsync(server => server.Id == id, cancellationToken);

    public async Task<(IReadOnlyCollection<Server> Items, long TotalCount)> ListAsync(
        int page,
        int pageSize,
        ServerEnvironment? environment,
        ServerStatus? status,
        bool includeDecommissioned,
        CancellationToken cancellationToken)
    {
        var query = _context.Servers.AsQueryable();

        if (!includeDecommissioned)
        {
            query = query.Where(server => server.DecommissionedAt == null);
        }

        if (environment is { } wantedEnvironment)
        {
            query = query.Where(server => server.Environment == wantedEnvironment);
        }

        if (status is { } wantedStatus)
        {
            query = query.Where(server => server.Status == wantedStatus);
        }

        var total = await query.LongCountAsync(cancellationToken);

        var items = await query
            .OrderBy(server => server.Name)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);

        return (items, total);
    }

    /// <summary>
    /// Active servers that have reported at least once.
    ///
    /// A server that has never reported is excluded on purpose: it cannot be
    /// "offline" because it was never online, and alerting on a machine somebody
    /// registered an hour ago and has not finished building would be noise.
    /// </summary>
    public async Task<IReadOnlyCollection<Server>> ListReportingAsync(CancellationToken cancellationToken) =>
        await _context.Servers
            .Where(server => server.DecommissionedAt == null && server.LastSeenAt != null)
            .ToListAsync(cancellationToken);

    public async Task AddAsync(Server server, CancellationToken cancellationToken) =>
        await _context.Servers.AddAsync(server, cancellationToken);

    public Task SaveChangesAsync(CancellationToken cancellationToken) => _context.SaveChangesAsync(cancellationToken);
}

internal sealed class AgentRepository : IAgentRepository
{
    private readonly ControlPlaneDbContext _context;

    public AgentRepository(ControlPlaneDbContext context)
    {
        _context = context;
    }

    public Task<Agent?> GetByIdAsync(Guid id, CancellationToken cancellationToken) =>
        _context.Agents.FirstOrDefaultAsync(agent => agent.Id == id, cancellationToken);

    public async Task<IReadOnlyCollection<Agent>> ListForServerAsync(Guid serverId, CancellationToken cancellationToken) =>
        await _context.Agents
            .Where(agent => agent.ServerId == serverId)
            .OrderByDescending(agent => agent.CreatedAt)
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyCollection<Agent>> ListAwaitingEnrolmentAsync(CancellationToken cancellationToken) =>
        await _context.Agents
            .Where(agent => agent.Status == AgentStatus.Provisioning && agent.ProvisioningTokenHash != null)
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyCollection<Agent>> ListEnrolledAsync(CancellationToken cancellationToken) =>
        await _context.Agents
            .Where(agent => agent.Status == AgentStatus.Online || agent.Status == AgentStatus.Offline)
            .ToListAsync(cancellationToken);

    public async Task AddAsync(Agent agent, CancellationToken cancellationToken) =>
        await _context.Agents.AddAsync(agent, cancellationToken);

    public Task SaveChangesAsync(CancellationToken cancellationToken) => _context.SaveChangesAsync(cancellationToken);
}

internal sealed class ServerMetricRepository : IServerMetricRepository
{
    private readonly ControlPlaneDbContext _context;

    public ServerMetricRepository(ControlPlaneDbContext context)
    {
        _context = context;
    }

    public async Task AddAsync(ServerMetric metric, CancellationToken cancellationToken) =>
        await _context.ServerMetrics.AddAsync(metric, cancellationToken);

    public async Task AddRangeAsync(IReadOnlyCollection<ServerMetric> metrics, CancellationToken cancellationToken) =>
        await _context.ServerMetrics.AddRangeAsync(metrics, cancellationToken);

    public async Task<IReadOnlyCollection<ServerMetric>> ListRecentAsync(
        Guid serverId,
        int limit,
        CancellationToken cancellationToken) =>
        await _context.ServerMetrics
            .AsNoTracking()
            .Where(metric => metric.ServerId == serverId)
            .OrderByDescending(metric => metric.CapturedAt)
            .Take(limit)
            .ToListAsync(cancellationToken);

    public Task<ServerMetric?> GetLatestAsync(Guid serverId, CancellationToken cancellationToken) =>
        _context.ServerMetrics
            .AsNoTracking()
            .Where(metric => metric.ServerId == serverId)
            .OrderByDescending(metric => metric.CapturedAt)
            .FirstOrDefaultAsync(cancellationToken);

    /// <summary>
    /// A set-based delete, issued as one statement.
    ///
    /// Loading a month of samples into memory to remove them would be its own
    /// outage on the largest table in the schema — which is precisely the table a
    /// retention job runs against.
    /// </summary>
    public Task<int> DeleteOlderThanAsync(DateTimeOffset cutoff, CancellationToken cancellationToken) =>
        _context.ServerMetrics
            .Where(metric => metric.CapturedAt < cutoff)
            .ExecuteDeleteAsync(cancellationToken);

    public Task SaveChangesAsync(CancellationToken cancellationToken) => _context.SaveChangesAsync(cancellationToken);
}

internal sealed class AlertRepository : IAlertRepository
{
    private readonly ControlPlaneDbContext _context;

    public AlertRepository(ControlPlaneDbContext context)
    {
        _context = context;
    }

    public Task<Alert?> GetByIdAsync(Guid id, CancellationToken cancellationToken) =>
        _context.Alerts.FirstOrDefaultAsync(alert => alert.Id == id, cancellationToken);

    public Task<Alert?> FindOpenAsync(string ruleKey, Guid sourceId, CancellationToken cancellationToken) =>
        _context.Alerts.FirstOrDefaultAsync(
            alert => alert.RuleKey == ruleKey && alert.SourceId == sourceId && alert.ResolvedAt == null,
            cancellationToken);

    public async Task<(IReadOnlyCollection<Alert> Items, long TotalCount)> ListAsync(
        int page,
        int pageSize,
        AlertSeverity? severity,
        AlertSource? source,
        bool openOnly,
        CancellationToken cancellationToken)
    {
        var query = _context.Alerts.AsQueryable();

        if (openOnly)
        {
            query = query.Where(alert => alert.ResolvedAt == null);
        }

        if (severity is { } wantedSeverity)
        {
            query = query.Where(alert => alert.Severity == wantedSeverity);
        }

        if (source is { } wantedSource)
        {
            query = query.Where(alert => alert.Source == wantedSource);
        }

        var total = await query.LongCountAsync(cancellationToken);

        // Most severe first, then newest. An operator scanning this list wants
        // the critical thing that just happened at the top, not the oldest
        // warning still open.
        var items = await query
            .OrderByDescending(alert => alert.Severity)
            .ThenByDescending(alert => alert.RaisedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);

        return (items, total);
    }

    public async Task<IReadOnlyCollection<Alert>> ListOpenForRuleAsync(string ruleKey, CancellationToken cancellationToken) =>
        await _context.Alerts
            .Where(alert => alert.RuleKey == ruleKey && alert.ResolvedAt == null)
            .ToListAsync(cancellationToken);

    public async Task AddAsync(Alert alert, CancellationToken cancellationToken) =>
        await _context.Alerts.AddAsync(alert, cancellationToken);

    public Task SaveChangesAsync(CancellationToken cancellationToken) => _context.SaveChangesAsync(cancellationToken);
}
