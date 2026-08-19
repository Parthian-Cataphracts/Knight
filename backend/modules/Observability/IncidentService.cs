using System.Globalization;
using Knight.Application.Abstractions.ControlPlane;
using Knight.Application.Abstractions.Time;
using Knight.Application.Exceptions;
using Microsoft.Extensions.Logging;
using Observability.Domain;

namespace Observability;

/// <summary>
/// The incident lifecycle.
///
/// Two things here are load-bearing and easy to lose in a refactor:
///
/// * **A rule opens at most one incident per subject.** The second time
///   `feature.install.failed` fires for the same store, it becomes a note on the
///   incident that is already open, or an escalation of its severity — never a
///   second incident. Forty incidents for one outage is the same failure as
///   forty alerts for one outage, one layer up.
/// * **Only a person resolves.** Nothing here closes an incident automatically
///   when the symptom clears, because the symptom clearing is not the problem
///   ending, and an incident that closed itself is an incident nobody wrote a
///   post-mortem for.
/// </summary>
internal sealed class IncidentService : IIncidentService
{
    private readonly IIncidentRepository _incidents;
    private readonly IRealtimeNotifier _realtime;
    private readonly IAuditTrail _audit;
    private readonly IDateTimeProvider _clock;
    private readonly ILogger<IncidentService> _logger;

    public IncidentService(
        IIncidentRepository incidents,
        IRealtimeNotifier realtime,
        IAuditTrail audit,
        IDateTimeProvider clock,
        ILogger<IncidentService> logger)
    {
        _incidents = incidents;
        _realtime = realtime;
        _audit = audit;
        _clock = clock;
        _logger = logger;
    }

    public Task<(IReadOnlyCollection<Incident> Items, long TotalCount)> ListAsync(
        IncidentStatus? status,
        IncidentSeverity? severity,
        Guid? storeId,
        bool openOnly,
        int page,
        int pageSize,
        CancellationToken cancellationToken) =>
        _incidents.ListAsync(
            status,
            severity,
            storeId,
            openOnly,
            Math.Max(page, 1),
            Math.Clamp(pageSize, 1, 200),
            cancellationToken);

    public async Task<Incident> GetAsync(Guid id, CancellationToken cancellationToken) =>
        await _incidents.GetByIdAsync(id, cancellationToken)
        ?? throw new NotFoundException("Incident", id);

    public async Task<IReadOnlyCollection<IncidentEvent>> ListTimelineAsync(Guid id, CancellationToken cancellationToken)
    {
        var incident = await _incidents.GetWithTimelineAsync(id, cancellationToken)
            ?? throw new NotFoundException("Incident", id);

        return incident.Timeline.OrderBy(entry => entry.OccurredAt).ToArray();
    }

    public async Task<Incident> OpenAsync(
        string title,
        IncidentSeverity severity,
        Guid actorId,
        Guid? customerId,
        Guid? storeId,
        Guid? serverId,
        string? summary,
        CancellationToken cancellationToken)
    {
        var now = _clock.UtcNow;

        var incident = Incident.Open(
            Guid.NewGuid(),
            now,
            await NextReferenceAsync(now, cancellationToken),
            title,
            severity,
            customerId,
            storeId,
            serverId,
            ruleKey: null,
            openedBy: actorId,
            summary: summary);

        await _incidents.AddAsync(incident, cancellationToken);
        await _incidents.SaveChangesAsync(cancellationToken);

        await _audit.RecordAsync(
            "incident.opened",
            "Incident",
            incident.Id.ToString(),
            customerId,
            cancellationToken,
            newValue: new { incident.Reference, incident.Title, incident.Severity });

        await BroadcastAsync(incident, "incidentOpened", cancellationToken);

        return incident;
    }

    public async Task<Incident?> OpenFromRuleAsync(
        string ruleKey,
        Guid subjectId,
        string title,
        IncidentSeverity severity,
        Guid? customerId,
        Guid? storeId,
        Guid? serverId,
        string detail,
        CancellationToken cancellationToken)
    {
        var now = _clock.UtcNow;
        var open = await _incidents.FindOpenByRuleAsync(ruleKey, subjectId, cancellationToken);

        if (open is not null)
        {
            // The condition is still true. That is worth a line on the timeline
            // and possibly an escalation, and nothing else.
            var existing = await _incidents.GetWithTimelineAsync(open.Id, cancellationToken) ?? open;

            var before = existing.Timeline.Select(entry => entry.Id).ToHashSet();

            existing.Escalate(severity, now, detail);

            _incidents.RegisterNewEvents(
                [.. existing.Timeline.Where(entry => !before.Contains(entry.Id))]);

            await _incidents.SaveChangesAsync(cancellationToken);

            return null;
        }

        var incident = Incident.Open(
            Guid.NewGuid(),
            now,
            await NextReferenceAsync(now, cancellationToken),
            title,
            severity,
            customerId,
            storeId,
            serverId,
            ruleKey,
            openedBy: null,
            summary: detail);

        // The rule's subject is what deduplication keys on, so it has to survive
        // on the record rather than only in the sweep that produced it.
        incident.AddNote(null, now, $"Subject: {subjectId}. {detail}");

        await _incidents.AddAsync(incident, cancellationToken);
        await _incidents.SaveChangesAsync(cancellationToken);

        await _audit.RecordAsync(
            "incident.opened.automatic",
            "Incident",
            incident.Id.ToString(),
            customerId,
            cancellationToken,
            newValue: new { incident.Reference, incident.Title, RuleKey = ruleKey, Subject = subjectId });

        await BroadcastAsync(incident, "incidentOpened", cancellationToken);

        return incident;
    }

    public Task<Incident> AcknowledgeAsync(Guid id, Guid actorId, string? note, CancellationToken cancellationToken) =>
        MutateAsync(id, "incident.acknowledged", cancellationToken, (incident, now) => incident.Acknowledge(actorId, now, note));

    public Task<Incident> MitigateAsync(Guid id, Guid actorId, string note, CancellationToken cancellationToken) =>
        MutateAsync(id, "incident.mitigated", cancellationToken, (incident, now) => incident.Mitigate(actorId, now, note));

    public Task<Incident> ResolveAsync(Guid id, Guid actorId, string? rootCause, CancellationToken cancellationToken) =>
        MutateAsync(id, "incident.resolved", cancellationToken, (incident, now) => incident.Resolve(actorId, now, rootCause));

    public Task<Incident> ReopenAsync(Guid id, Guid actorId, string reason, CancellationToken cancellationToken) =>
        MutateAsync(id, "incident.reopened", cancellationToken, (incident, now) => incident.Reopen(actorId, now, reason));

    public Task<Incident> AddNoteAsync(Guid id, Guid actorId, string message, CancellationToken cancellationToken) =>
        MutateAsync(id, "incident.note.added", cancellationToken, (incident, now) => incident.AddNote(actorId, now, message));

    /// <summary>
    /// Loads with the timeline, applies the change, saves and audits. Every
    /// mutation goes through here so that none of them can forget the timeline
    /// is part of the aggregate: loading without it and then appending would
    /// silently drop the history EF Core never tracked.
    /// </summary>
    private async Task<Incident> MutateAsync(
        Guid id,
        string action,
        CancellationToken cancellationToken,
        Action<Incident, DateTimeOffset> change)
    {
        var incident = await _incidents.GetWithTimelineAsync(id, cancellationToken)
            ?? throw new NotFoundException("Incident", id);

        var before = incident.Timeline.Select(entry => entry.Id).ToHashSet();

        change(incident, _clock.UtcNow);

        // Whatever the change appended has to be declared new explicitly: the
        // domain assigns its own ids, and persistence would otherwise read a set
        // id as "this row already exists".
        _incidents.RegisterNewEvents(
            [.. incident.Timeline.Where(entry => !before.Contains(entry.Id))]);

        await _incidents.SaveChangesAsync(cancellationToken);

        await _audit.RecordAsync(
            action,
            "Incident",
            incident.Id.ToString(),
            incident.CustomerId,
            cancellationToken,
            newValue: new { incident.Reference, incident.Status, incident.Severity });

        await BroadcastAsync(incident, "incidentChanged", cancellationToken);

        return incident;
    }

    /// <summary>
    /// <c>INC-2026-0042</c>. The year is in the reference so the sequence resets
    /// annually and stays short enough to say out loud during an outage.
    /// </summary>
    private async Task<string> NextReferenceAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        var year = now.Year;
        var sequence = await _incidents.NextReferenceSequenceAsync(year, cancellationToken);

        return string.Create(CultureInfo.InvariantCulture, $"INC-{year}-{sequence:D4}");
    }

    private async Task BroadcastAsync(Incident incident, string @event, CancellationToken cancellationToken)
    {
        try
        {
            await _realtime.BroadcastAsync(
                new RealtimeMessage(
                    @event,
                    incident.CustomerId,
                    new
                    {
                        id = incident.Id,
                        reference = incident.Reference,
                        title = incident.Title,
                        severity = incident.Severity.ToString(),
                        status = incident.Status.ToString(),
                    }),
                cancellationToken);
        }
        catch (Exception exception)
        {
            // Realtime is an improvement on polling, never something correctness
            // depends on. The incident is saved; the dashboard will catch up on
            // its next fetch.
            _logger.LogWarning(exception, "Failed to broadcast {Event} for incident {IncidentId}.", @event, incident.Id);
        }
    }
}
