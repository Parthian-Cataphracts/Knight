using Ingestion.Domain;
using Microsoft.EntityFrameworkCore;
using Observability.Domain;

namespace Knight.Infrastructure.ControlPlane.Repositories;

/// <summary>
/// Persistence for error grouping, incidents and notifications.
///
/// Every query here goes through the context's global isolation filter, so none
/// of these methods carries a customer parameter: a customer principal reading
/// error groups sees theirs, a platform principal sees all, and neither can be
/// changed by getting a `Where` clause wrong in this file
/// (docs/authorization.md §3).
/// </summary>
internal sealed class ErrorGroupRepository : IErrorGroupRepository
{
    private readonly ControlPlaneDbContext _context;

    public ErrorGroupRepository(ControlPlaneDbContext context)
    {
        _context = context;
    }

    public Task<ErrorGroup?> GetByIdAsync(Guid id, CancellationToken cancellationToken) =>
        _context.ErrorGroups.FirstOrDefaultAsync(group => group.Id == id, cancellationToken);

    public Task<ErrorGroup?> FindByFingerprintAsync(
        Guid storeId,
        string fingerprint,
        int fingerprintVersion,
        CancellationToken cancellationToken) =>
        _context.ErrorGroups.FirstOrDefaultAsync(
            group => group.StoreId == storeId &&
                     group.Fingerprint == fingerprint &&
                     group.FingerprintVersion == fingerprintVersion,
            cancellationToken);

    public async Task<IReadOnlyCollection<ErrorGroup>> FindByFingerprintsAsync(
        Guid storeId,
        IReadOnlyCollection<string> fingerprints,
        int fingerprintVersion,
        CancellationToken cancellationToken)
    {
        if (fingerprints.Count == 0)
        {
            return [];
        }

        // Tracked, because the caller is about to increment counters on whatever
        // comes back. This is the one read in the module that is deliberately not
        // a projection.
        return await _context.ErrorGroups
            .Where(group => group.StoreId == storeId &&
                            group.FingerprintVersion == fingerprintVersion &&
                            fingerprints.Contains(group.Fingerprint))
            .ToArrayAsync(cancellationToken);
    }

    public async Task<(IReadOnlyCollection<ErrorGroup> Items, long TotalCount)> ListAsync(
        Guid? storeId,
        ErrorGroupStatus? status,
        string? environment,
        int page,
        int pageSize,
        CancellationToken cancellationToken)
    {
        var query = _context.ErrorGroups.AsNoTracking();

        if (storeId is { } wantedStore)
        {
            query = query.Where(group => group.StoreId == wantedStore);
        }

        if (status is { } wantedStatus)
        {
            query = query.Where(group => group.Status == wantedStatus);
        }

        if (!string.IsNullOrWhiteSpace(environment))
        {
            query = query.Where(group => group.Environment == environment);
        }

        var total = await query.LongCountAsync(cancellationToken);

        var items = await query
            // Most recently seen first: an operator opening this screen wants to
            // know what is broken now, not what was broken most often ever.
            .OrderByDescending(group => group.LastSeenAt)
            .ThenBy(group => group.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToArrayAsync(cancellationToken);

        return (items, total);
    }

    public async Task<IReadOnlyCollection<ErrorGroup>> ListSeenSinceAsync(
        DateTimeOffset since,
        CancellationToken cancellationToken) =>
        await _context.ErrorGroups
            .Where(group => group.LastSeenAt >= since && group.Status == ErrorGroupStatus.New)
            .ToArrayAsync(cancellationToken);

    public async Task AddAsync(ErrorGroup group, CancellationToken cancellationToken) =>
        await _context.ErrorGroups.AddAsync(group, cancellationToken);

    public Task SaveChangesAsync(CancellationToken cancellationToken) =>
        _context.SaveChangesAsync(cancellationToken);
}

/// <summary>
/// Reads the raw occurrences behind a group.
///
/// These rows belong to ingestion's table, not to grouping: the error stream is
/// the record of what stores reported, and grouping annotates it. Reading it from
/// here rather than adding a navigation keeps that ownership honest, and keeps
/// the highest-volume table in the schema free of a relationship that would make
/// every insert check one.
/// </summary>
internal sealed class ErrorGroupEventReader : IErrorGroupEventReader
{
    private readonly ControlPlaneDbContext _context;

    public ErrorGroupEventReader(ControlPlaneDbContext context)
    {
        _context = context;
    }

    public async Task<IReadOnlyCollection<ErrorGroupEventSample>> ListSamplesAsync(
        Guid groupId,
        int limit,
        CancellationToken cancellationToken) =>
        await _context.StoreErrorEvents
            .AsNoTracking()
            // Only sampled events carry a stack trace; the rest were stripped on
            // the way in and would render as an empty detail panel.
            .Where(error => error.ErrorGroupId == groupId && error.IsSample)
            .OrderByDescending(error => error.OccurredAt)
            .Take(limit)
            .Select(error => new ErrorGroupEventSample(
                error.Id,
                error.OccurredAt,
                error.StoreVersion,
                error.RequestId,
                error.TraceId,
                error.StackTrace,
                error.Message,
                error.Endpoint,
                error.StatusCode))
            .ToArrayAsync(cancellationToken);

    public Task<int> CountSinceAsync(Guid groupId, DateTimeOffset since, CancellationToken cancellationToken) =>
        _context.StoreErrorEvents
            .AsNoTracking()
            .CountAsync(error => error.ErrorGroupId == groupId && error.OccurredAt >= since, cancellationToken);
}

internal sealed class IncidentRepository : IIncidentRepository
{
    private readonly ControlPlaneDbContext _context;

    public IncidentRepository(ControlPlaneDbContext context)
    {
        _context = context;
    }

    public Task<Incident?> GetByIdAsync(Guid id, CancellationToken cancellationToken) =>
        _context.Incidents.FirstOrDefaultAsync(incident => incident.Id == id, cancellationToken);

    public Task<Incident?> GetWithTimelineAsync(Guid id, CancellationToken cancellationToken) =>
        _context.Incidents
            .Include(incident => incident.Timeline)
            .FirstOrDefaultAsync(incident => incident.Id == id, cancellationToken);

    public Task<Incident?> FindOpenByRuleAsync(string ruleKey, Guid subjectId, CancellationToken cancellationToken) =>
        _context.Incidents
            .Where(incident => incident.RuleKey == ruleKey && incident.Status != IncidentStatus.Resolved)
            // The subject is recorded on the opening note rather than as a column,
            // because only rule-opened incidents have one and a column would be
            // null for every incident a person raised. The candidate set is small
            // — open incidents for one rule — so the scan is cheap.
            .Where(incident => incident.Timeline.Any(entry => entry.Message.Contains($"Subject: {subjectId}")))
            .FirstOrDefaultAsync(cancellationToken);

    public async Task<(IReadOnlyCollection<Incident> Items, long TotalCount)> ListAsync(
        IncidentStatus? status,
        IncidentSeverity? severity,
        Guid? storeId,
        bool openOnly,
        int page,
        int pageSize,
        CancellationToken cancellationToken)
    {
        var query = _context.Incidents.AsNoTracking();

        if (openOnly)
        {
            query = query.Where(incident => incident.Status != IncidentStatus.Resolved);
        }

        if (status is { } wantedStatus)
        {
            query = query.Where(incident => incident.Status == wantedStatus);
        }

        if (severity is { } wantedSeverity)
        {
            query = query.Where(incident => incident.Severity == wantedSeverity);
        }

        if (storeId is { } wantedStore)
        {
            query = query.Where(incident => incident.StoreId == wantedStore);
        }

        var total = await query.LongCountAsync(cancellationToken);

        var items = await query
            .OrderByDescending(incident => incident.OpenedAt)
            .ThenBy(incident => incident.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToArrayAsync(cancellationToken);

        return (items, total);
    }

    /// <summary>
    /// Reserves the next reference by incrementing the counter row in one atomic
    /// statement. Two rules opening an incident at the same instant are serialised
    /// by the row lock, so neither can read a value the other is about to take —
    /// which a read-then-write in application code could not guarantee.
    /// </summary>
    public async Task<int> NextReferenceSequenceAsync(int year, CancellationToken cancellationToken)
    {
        var reserved = await _context.Database
            .SqlQuery<int>($"""
                INSERT INTO control.incident_reference_sequences ("Year", "LastValue")
                VALUES ({year}, 1)
                ON CONFLICT ("Year")
                DO UPDATE SET "LastValue" = control.incident_reference_sequences."LastValue" + 1
                RETURNING "LastValue"
                """)
            .ToArrayAsync(cancellationToken);

        return reserved.Single();
    }

    public async Task AddAsync(Incident incident, CancellationToken cancellationToken) =>
        await _context.Incidents.AddAsync(incident, cancellationToken);

    public void RegisterNewEvents(IReadOnlyCollection<IncidentEvent> entries)
    {
        foreach (var entry in entries)
        {
            _context.Entry(entry).State = EntityState.Added;
        }
    }

    public Task SaveChangesAsync(CancellationToken cancellationToken) =>
        _context.SaveChangesAsync(cancellationToken);
}

internal sealed class NotificationRepository : INotificationRepository
{
    private readonly ControlPlaneDbContext _context;

    public NotificationRepository(ControlPlaneDbContext context)
    {
        _context = context;
    }

    public Task<NotificationChannel?> GetChannelAsync(Guid id, CancellationToken cancellationToken) =>
        _context.NotificationChannels.FirstOrDefaultAsync(channel => channel.Id == id, cancellationToken);

    public async Task<IReadOnlyCollection<NotificationChannel>> ListChannelsAsync(
        Guid? customerId,
        bool includeDisabled,
        CancellationToken cancellationToken)
    {
        var query = _context.NotificationChannels.AsQueryable();

        if (customerId is { } wanted)
        {
            query = query.Where(channel => channel.CustomerId == wanted);
        }

        if (!includeDisabled)
        {
            query = query.Where(channel => channel.IsEnabled);
        }

        return await query.OrderBy(channel => channel.Name).ToArrayAsync(cancellationToken);
    }

    public async Task<IReadOnlyCollection<NotificationChannel>> ListRoutableAsync(
        Guid? customerId,
        CancellationToken cancellationToken) =>
        await _context.NotificationChannels
            // A customer's own channels, plus the platform channels that watch
            // everything. The isolation filter still applies on top of this, so a
            // customer principal never sees a platform channel — but the sweep
            // that queues notifications runs in platform scope and must.
            .Where(channel => channel.IsEnabled &&
                              (channel.CustomerId == null ||
                               (customerId != null && channel.CustomerId == customerId)))
            .ToArrayAsync(cancellationToken);

    public async Task AddChannelAsync(NotificationChannel channel, CancellationToken cancellationToken) =>
        await _context.NotificationChannels.AddAsync(channel, cancellationToken);

    public Task<NotificationDelivery?> GetDeliveryAsync(Guid id, CancellationToken cancellationToken) =>
        _context.NotificationDeliveries.FirstOrDefaultAsync(delivery => delivery.Id == id, cancellationToken);

    public async Task<IReadOnlyCollection<NotificationDelivery>> ListDueAsync(
        DateTimeOffset now,
        int limit,
        CancellationToken cancellationToken) =>
        await _context.NotificationDeliveries
            .Where(delivery => delivery.Status == NotificationDeliveryStatus.Pending &&
                               delivery.NextAttemptAt <= now)
            // Oldest first, so a backlog drains in the order things went wrong
            // rather than newest-first, which would starve the original problem.
            .OrderBy(delivery => delivery.NextAttemptAt)
            .Take(limit)
            .ToArrayAsync(cancellationToken);

    public async Task<(IReadOnlyCollection<NotificationDelivery> Items, long TotalCount)> ListDeliveriesAsync(
        Guid? channelId,
        NotificationDeliveryStatus? status,
        int page,
        int pageSize,
        CancellationToken cancellationToken)
    {
        var query = _context.NotificationDeliveries.AsNoTracking();

        if (channelId is { } wantedChannel)
        {
            query = query.Where(delivery => delivery.ChannelId == wantedChannel);
        }

        if (status is { } wantedStatus)
        {
            query = query.Where(delivery => delivery.Status == wantedStatus);
        }

        var total = await query.LongCountAsync(cancellationToken);

        var items = await query
            .OrderByDescending(delivery => delivery.CreatedAt)
            .ThenBy(delivery => delivery.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToArrayAsync(cancellationToken);

        return (items, total);
    }

    public Task<bool> HasRecentAsync(
        Guid channelId,
        string ruleKey,
        Guid subjectId,
        DateTimeOffset since,
        CancellationToken cancellationToken) =>
        _context.NotificationDeliveries
            .AsNoTracking()
            .AnyAsync(
                delivery => delivery.ChannelId == channelId &&
                            delivery.RuleKey == ruleKey &&
                            delivery.SubjectId == subjectId &&
                            delivery.CreatedAt >= since,
                cancellationToken);

    public async Task AddDeliveryAsync(NotificationDelivery delivery, CancellationToken cancellationToken) =>
        await _context.NotificationDeliveries.AddAsync(delivery, cancellationToken);

    public async Task AddDeliveriesAsync(
        IReadOnlyCollection<NotificationDelivery> deliveries,
        CancellationToken cancellationToken) =>
        await _context.NotificationDeliveries.AddRangeAsync(deliveries, cancellationToken);

    public Task SaveChangesAsync(CancellationToken cancellationToken) =>
        _context.SaveChangesAsync(cancellationToken);
}
