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

    public async Task AddBackupAsync(StoreBackup backup, CancellationToken cancellationToken) =>
        await _context.StoreBackups.AddAsync(backup, cancellationToken);

    public async Task<IReadOnlyCollection<StoreBackup>> ListBackupsAsync(
        Guid storeId,
        int limit,
        CancellationToken cancellationToken) =>
        await _context.StoreBackups
            .AsNoTracking()
            .Where(backup => backup.StoreId == storeId)
            .OrderByDescending(backup => backup.StartedAt)
            .Take(limit)
            .ToArrayAsync(cancellationToken);

    public Task<StoreBackup?> GetLatestBackupAsync(Guid storeId, CancellationToken cancellationToken) =>
        _context.StoreBackups
            .AsNoTracking()
            .Where(backup => backup.StoreId == storeId)
            .OrderByDescending(backup => backup.StartedAt)
            .FirstOrDefaultAsync(cancellationToken);

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
        LogFilter filter,
        int page,
        int pageSize,
        CancellationToken cancellationToken)
    {
        var ordered = FilteredLogs(filter);
        var total = await ordered.LongCountAsync(cancellationToken);
        var items = await ordered.Skip((page - 1) * pageSize).Take(pageSize).ToArrayAsync(cancellationToken);

        return (items, total);
    }

    public async Task<IReadOnlyCollection<StoreLogEntry>> ExportLogsAsync(
        LogFilter filter,
        int max,
        CancellationToken cancellationToken) =>
        await FilteredLogs(filter).Take(max).ToArrayAsync(cancellationToken);

    /// <summary>
    /// The log stream narrowed by a filter, newest first. One place so the export
    /// and the paged read can never disagree about what a filter means.
    /// </summary>
    private IOrderedQueryable<StoreLogEntry> FilteredLogs(LogFilter filter)
    {
        var query = _context.StoreLogEntries.AsQueryable();

        if (filter.StoreId is { } id)
        {
            query = query.Where(entry => entry.StoreId == id);
        }

        if (!string.IsNullOrWhiteSpace(filter.Level))
        {
            // An exact level, compared against the stored form, which is
            // upper-cased on the way in; normalisation into the dashboard's
            // vocabulary happens on read. The more specific request wins over a
            // minimum severity, so it is applied first and alone.
            var normalised = filter.Level.Trim().ToUpperInvariant();
            query = query.Where(entry => entry.Level == normalised);
        }
        else if (LogSeverity.TokensAtOrAbove(filter.MinSeverity) is { } tokens)
        {
            // Everything at or above a severity — the errors, warnings and alerts
            // pulled out of the noise. A store's raw tokens vary, so the filter is
            // the set of tokens in the wanted buckets rather than a comparison.
            query = query.Where(entry => tokens.Contains(entry.Level));
        }

        if (filter.From is { } from)
        {
            query = query.Where(entry => entry.Timestamp >= from);
        }

        if (filter.To is { } to)
        {
            query = query.Where(entry => entry.Timestamp <= to);
        }

        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            // Case-insensitive substring on the message. The term's LIKE
            // metacharacters are escaped so a search for "50%" is not a wildcard.
            var term = $"%{Escape(filter.Search.Trim())}%";
            query = query.Where(entry => EF.Functions.ILike(entry.Message, term, "\\"));
        }

        return query.OrderByDescending(entry => entry.Timestamp).ThenBy(entry => entry.Id);
    }

    private static string Escape(string term) =>
        term.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");

    public Task SaveChangesAsync(CancellationToken cancellationToken) => _context.SaveChangesAsync(cancellationToken);
}
