using Knight.Application.Abstractions.ControlPlane;
using Knight.Application.Abstractions.Time;
using Knight.Application.Exceptions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Observability.Domain;

namespace Observability;

/// <summary>
/// Groups incoming errors, and lets an operator act on the groups.
///
/// The grouping half runs inside ingestion's request, on the hottest write path
/// KNIGHT has, and its design follows from one rule: **telemetry must never be
/// lost because analysis of it failed**. So the batch is grouped in two queries
/// rather than two per event, and every failure mode leaves the raw events
/// stored and merely ungrouped. An ungrouped error is a small loss of
/// convenience; a rejected batch is a loss of the only evidence a store had.
/// </summary>
internal sealed class ErrorService : IErrorService, IErrorGrouping
{
    private readonly IErrorGroupRepository _groups;
    private readonly IErrorGroupEventReader _events;
    private readonly IAlertRaiser _alerts;
    private readonly IAuditTrail _audit;
    private readonly IDateTimeProvider _clock;
    private readonly ILogger<ErrorService> _logger;
    private readonly ObservabilityOptions _options;

    public ErrorService(
        IErrorGroupRepository groups,
        IErrorGroupEventReader events,
        IAlertRaiser alerts,
        IAuditTrail audit,
        IDateTimeProvider clock,
        ILogger<ErrorService> logger,
        IOptions<ObservabilityOptions> options)
    {
        _groups = groups;
        _events = events;
        _alerts = alerts;
        _audit = audit;
        _clock = clock;
        _logger = logger;
        _options = options.Value;
    }

    public async Task<IReadOnlyCollection<ErrorGroupAssignment>> GroupAsync(
        IReadOnlyCollection<ErrorToGroup> events,
        CancellationToken cancellationToken)
    {
        if (events.Count == 0)
        {
            return [];
        }

        var fingerprints = new Dictionary<Guid, (ErrorToGroup Event, ErrorFingerprintResult Print)>(events.Count);

        foreach (var candidate in events)
        {
            fingerprints[candidate.EventId] = (
                candidate,
                ErrorFingerprint.Compute(
                    candidate.StoreId,
                    candidate.Environment,
                    candidate.ExceptionType,
                    candidate.Message,
                    candidate.Endpoint,
                    candidate.StackTrace));
        }

        var now = _clock.UtcNow;
        var assignments = new List<ErrorGroupAssignment>(events.Count);
        var regressions = new List<ErrorGroup>();

        // A batch normally comes from one store; grouping per store keeps the
        // lookup to one query per store rather than one per distinct fingerprint.
        foreach (var perStore in fingerprints.Values.GroupBy(entry => entry.Event.StoreId))
        {
            var wanted = perStore.Select(entry => entry.Print.Fingerprint).Distinct().ToArray();

            var existing = (await _groups.FindByFingerprintsAsync(
                    perStore.Key,
                    wanted,
                    ErrorFingerprint.Version,
                    cancellationToken))
                .ToDictionary(group => group.Fingerprint, StringComparer.Ordinal);

            // Ordered by occurrence so that "first seen" is the earliest event in
            // the batch rather than whichever the store happened to serialise
            // first.
            foreach (var (candidate, print) in perStore.OrderBy(entry => entry.Event.OccurredAt))
            {
                if (!existing.TryGetValue(print.Fingerprint, out var group))
                {
                    group = ErrorGroup.Open(
                        Guid.NewGuid(),
                        candidate.OccurredAt < now ? candidate.OccurredAt : now,
                        candidate.CustomerId,
                        candidate.StoreId,
                        print,
                        candidate.Environment,
                        candidate.ExceptionType,
                        candidate.StoreVersion);

                    await _groups.AddAsync(group, cancellationToken);
                    existing[print.Fingerprint] = group;
                }

                var keepSample = group.SampleCount < _options.MaxSamplesPerGroup;

                if (group.Record(candidate.OccurredAt, now, candidate.StoreVersion, keepSample))
                {
                    regressions.Add(group);
                }

                assignments.Add(new ErrorGroupAssignment(candidate.EventId, group.Id, keepSample));
            }
        }

        await _groups.SaveChangesAsync(cancellationToken);

        // Alerting happens after the save, so a failure to alert cannot roll back
        // the grouping. A regression that went unalerted is still visible on the
        // errors screen; a group that was never written is invisible everywhere.
        foreach (var group in regressions)
        {
            await RaiseRegressionAsync(group, cancellationToken);
        }

        return assignments;
    }

    private async Task RaiseRegressionAsync(ErrorGroup group, CancellationToken cancellationToken)
    {
        try
        {
            await _alerts.RaiseAsync(
                ObservabilityRules.ErrorRegression,
                nameof(NotificationSeverity.Warning),
                "Store",
                group.Id,
                group.CustomerId,
                $"A resolved error has returned: {group.Title}",
                cancellationToken);
        }
        catch (Exception exception)
        {
            _logger.LogError(
                exception,
                "Failed to raise a regression alert for error group {GroupId}; the regression is recorded regardless.",
                group.Id);
        }
    }

    public Task<(IReadOnlyCollection<ErrorGroup> Items, long TotalCount)> ListGroupsAsync(
        Guid? storeId,
        ErrorGroupStatus? status,
        string? environment,
        int page,
        int pageSize,
        CancellationToken cancellationToken) =>
        _groups.ListAsync(
            storeId,
            status,
            string.IsNullOrWhiteSpace(environment) ? null : environment.Trim(),
            Math.Max(page, 1),
            Math.Clamp(pageSize, 1, 200),
            cancellationToken);

    public async Task<ErrorGroup> GetGroupAsync(Guid id, CancellationToken cancellationToken) =>
        await _groups.GetByIdAsync(id, cancellationToken)
        ?? throw new NotFoundException("Error group", id);

    public async Task<IReadOnlyCollection<ErrorGroupEventSample>> ListSamplesAsync(
        Guid groupId,
        int limit,
        CancellationToken cancellationToken)
    {
        // Resolving the group first is what applies the isolation filter: without
        // it, a caller could ask for samples by a group id they were never
        // allowed to see.
        _ = await GetGroupAsync(groupId, cancellationToken);

        return await _events.ListSamplesAsync(
            groupId,
            Math.Clamp(limit, 1, _options.MaxSamplesPerGroup),
            cancellationToken);
    }

    public async Task<ErrorGroup> AcknowledgeAsync(Guid id, Guid userId, CancellationToken cancellationToken)
    {
        var group = await GetGroupAsync(id, cancellationToken);

        group.Acknowledge(userId, _clock.UtcNow);

        await CommitAsync(group, "error.group.acknowledged", cancellationToken);

        return group;
    }

    public async Task<ErrorGroup> ResolveAsync(Guid id, Guid userId, string? inVersion, CancellationToken cancellationToken)
    {
        var group = await GetGroupAsync(id, cancellationToken);

        group.Resolve(userId, _clock.UtcNow, inVersion);

        // The alert, if any, goes with it: leaving an open alert behind for a
        // problem somebody has declared fixed is how alert lists stop being read.
        await _alerts.ResolveAsync(ObservabilityRules.ErrorSpike, group.Id, cancellationToken);
        await _alerts.ResolveAsync(ObservabilityRules.ErrorRegression, group.Id, cancellationToken);

        await CommitAsync(group, "error.group.resolved", cancellationToken);

        return group;
    }

    public async Task<ErrorGroup> IgnoreAsync(Guid id, Guid userId, CancellationToken cancellationToken)
    {
        var group = await GetGroupAsync(id, cancellationToken);

        group.Ignore(userId, _clock.UtcNow);

        await _alerts.ResolveAsync(ObservabilityRules.ErrorSpike, group.Id, cancellationToken);
        await _alerts.ResolveAsync(ObservabilityRules.ErrorRegression, group.Id, cancellationToken);

        await CommitAsync(group, "error.group.ignored", cancellationToken);

        return group;
    }

    public async Task<ErrorGroup> ReopenAsync(Guid id, Guid userId, CancellationToken cancellationToken)
    {
        var group = await GetGroupAsync(id, cancellationToken);

        group.Reopen(_clock.UtcNow);

        await CommitAsync(group, "error.group.reopened", cancellationToken);

        return group;
    }

    private async Task CommitAsync(ErrorGroup group, string action, CancellationToken cancellationToken)
    {
        await _groups.SaveChangesAsync(cancellationToken);

        await _audit.RecordAsync(
            action,
            "ErrorGroup",
            group.Id.ToString(),
            group.CustomerId,
            cancellationToken,
            newValue: new { group.Status, group.Title, group.OccurrenceCount });
    }
}
