using Ingestion.Domain;
using Microsoft.EntityFrameworkCore;
using Stores.Domain;

namespace Knight.Infrastructure.ControlPlane.Repositories;

/// <summary>
/// Health observations and deployments. Reads are ordered newest-first and
/// always bounded: these tables grow with traffic, and an unbounded read of one
/// would be a denial of service against KNIGHT rather than a slow page.
/// </summary>
internal sealed class StoreTelemetryRepository : IStoreTelemetryRepository
{
    private readonly ControlPlaneDbContext _context;

    public StoreTelemetryRepository(ControlPlaneDbContext context)
    {
        _context = context;
    }

    public async Task AddHealthCheckAsync(StoreHealthCheck healthCheck, CancellationToken cancellationToken) =>
        await _context.StoreHealthChecks.AddAsync(healthCheck, cancellationToken);

    public async Task AddDeploymentAsync(StoreDeployment deployment, CancellationToken cancellationToken) =>
        await _context.StoreDeployments.AddAsync(deployment, cancellationToken);

    public async Task<IReadOnlyCollection<StoreHealthCheck>> ListHealthChecksAsync(
        Guid storeId,
        int limit,
        CancellationToken cancellationToken) =>
        await _context.StoreHealthChecks
            .Where(check => check.StoreId == storeId)
            .OrderByDescending(check => check.CheckedAt)
            .Take(limit)
            .ToArrayAsync(cancellationToken);

    public async Task<IReadOnlyCollection<StoreDeployment>> ListDeploymentsAsync(
        Guid storeId,
        int limit,
        CancellationToken cancellationToken) =>
        await _context.StoreDeployments
            .Where(deployment => deployment.StoreId == storeId)
            .OrderByDescending(deployment => deployment.DeployedAt)
            .Take(limit)
            .ToArrayAsync(cancellationToken);

    public Task<StoreDeployment?> GetLatestDeploymentAsync(Guid storeId, CancellationToken cancellationToken) =>
        _context.StoreDeployments
            .Where(deployment => deployment.StoreId == storeId)
            .OrderByDescending(deployment => deployment.DetectedAt)
            .FirstOrDefaultAsync(cancellationToken);

    /// <summary>
    /// One row per store, read in a single query. The alternative — a query per
    /// store — is what turns a stores list into a hundred round trips as soon as
    /// a deployment has a hundred stores in it.
    /// </summary>
    public async Task<IReadOnlyDictionary<Guid, StoreHealthCheck>> LatestHealthChecksAsync(
        IReadOnlyCollection<Guid> storeIds,
        CancellationToken cancellationToken)
    {
        if (storeIds.Count == 0)
        {
            return new Dictionary<Guid, StoreHealthCheck>();
        }

        var latest = await _context.StoreHealthChecks
            .Where(check => storeIds.Contains(check.StoreId))
            .GroupBy(check => check.StoreId)
            .Select(group => group.OrderByDescending(check => check.CheckedAt).First())
            .ToArrayAsync(cancellationToken);

        return latest.ToDictionary(check => check.StoreId);
    }

    public Task SaveChangesAsync(CancellationToken cancellationToken) => _context.SaveChangesAsync(cancellationToken);
}

/// <summary>
/// The telemetry stores push. Writes go in as batches — one round trip per
/// batch, whatever its size — because this is the highest-volume write path in
/// the system and the only one whose rate KNIGHT does not control.
/// </summary>
internal sealed class IngestionRepository : IIngestionRepository
{
    private readonly ControlPlaneDbContext _context;

    public IngestionRepository(ControlPlaneDbContext context)
    {
        _context = context;
    }

    public async Task AddErrorsAsync(IReadOnlyCollection<StoreErrorEvent> events, CancellationToken cancellationToken) =>
        await _context.StoreErrorEvents.AddRangeAsync(events, cancellationToken);

    public async Task AddEventsAsync(IReadOnlyCollection<StoreLifecycleEvent> events, CancellationToken cancellationToken) =>
        await _context.StoreEvents.AddRangeAsync(events, cancellationToken);

    public async Task AddLogsAsync(IReadOnlyCollection<StoreLogEntry> entries, CancellationToken cancellationToken) =>
        await _context.StoreLogEntries.AddRangeAsync(entries, cancellationToken);

    public async Task<(IReadOnlyCollection<StoreErrorEvent> Items, long TotalCount)> ListErrorsAsync(
        Guid? storeId,
        int page,
        int pageSize,
        CancellationToken cancellationToken)
    {
        var query = _context.StoreErrorEvents.AsQueryable();

        if (storeId is { } id)
        {
            query = query.Where(e => e.StoreId == id);
        }

        var ordered = query.OrderByDescending(e => e.OccurredAt).ThenBy(e => e.Id);
        var total = await ordered.LongCountAsync(cancellationToken);
        var items = await ordered.Skip((page - 1) * pageSize).Take(pageSize).ToArrayAsync(cancellationToken);

        return (items, total);
    }

    public async Task<(IReadOnlyCollection<StoreLifecycleEvent> Items, long TotalCount)> ListEventsAsync(
        Guid? storeId,
        int page,
        int pageSize,
        CancellationToken cancellationToken)
    {
        var query = _context.StoreEvents.AsQueryable();

        if (storeId is { } id)
        {
            query = query.Where(e => e.StoreId == id);
        }

        var ordered = query.OrderByDescending(e => e.OccurredAt).ThenBy(e => e.Id);
        var total = await ordered.LongCountAsync(cancellationToken);
        var items = await ordered.Skip((page - 1) * pageSize).Take(pageSize).ToArrayAsync(cancellationToken);

        return (items, total);
    }

    public async Task<(IReadOnlyCollection<StoreLogEntry> Items, long TotalCount)> ListLogsAsync(
        Guid? storeId,
        string? level,
        int page,
        int pageSize,
        CancellationToken cancellationToken)
    {
        var query = _context.StoreLogEntries.AsQueryable();

        if (storeId is { } id)
        {
            query = query.Where(entry => entry.StoreId == id);
        }

        if (!string.IsNullOrWhiteSpace(level))
        {
            // Compared against the stored form, which is upper-cased on the way
            // in; normalisation into the dashboard's vocabulary happens on read.
            var normalised = level.Trim().ToUpperInvariant();
            query = query.Where(entry => entry.Level == normalised);
        }

        var ordered = query.OrderByDescending(entry => entry.Timestamp).ThenBy(entry => entry.Id);
        var total = await ordered.LongCountAsync(cancellationToken);
        var items = await ordered.Skip((page - 1) * pageSize).Take(pageSize).ToArrayAsync(cancellationToken);

        return (items, total);
    }

    public Task SaveChangesAsync(CancellationToken cancellationToken) => _context.SaveChangesAsync(cancellationToken);
}
