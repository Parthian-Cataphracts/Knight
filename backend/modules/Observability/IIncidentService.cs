using Observability.Domain;

namespace Observability;

/// <summary>
/// Opening, working and closing incidents.
///
/// The write methods take an actor id because every one of them lands on the
/// timeline, and a timeline entry with no author is worth very little in the
/// review afterwards. The one exception is <see cref="OpenFromRuleAsync"/>,
/// which records the rule instead.
/// </summary>
public interface IIncidentService
{
    Task<(IReadOnlyCollection<Incident> Items, long TotalCount)> ListAsync(
        IncidentStatus? status,
        IncidentSeverity? severity,
        Guid? storeId,
        bool openOnly,
        int page,
        int pageSize,
        CancellationToken cancellationToken);

    Task<Incident> GetAsync(Guid id, CancellationToken cancellationToken);

    Task<IReadOnlyCollection<IncidentEvent>> ListTimelineAsync(Guid id, CancellationToken cancellationToken);

    Task<Incident> OpenAsync(
        string title,
        IncidentSeverity severity,
        Guid actorId,
        Guid? customerId,
        Guid? storeId,
        Guid? serverId,
        string? summary,
        CancellationToken cancellationToken);

    /// <summary>
    /// Opens an incident on behalf of a rule, or escalates the one that rule
    /// already has open for this subject. Returns null when nothing needed doing.
    /// </summary>
    Task<Incident?> OpenFromRuleAsync(
        string ruleKey,
        Guid subjectId,
        string title,
        IncidentSeverity severity,
        Guid? customerId,
        Guid? storeId,
        Guid? serverId,
        string detail,
        CancellationToken cancellationToken);

    Task<Incident> AcknowledgeAsync(Guid id, Guid actorId, string? note, CancellationToken cancellationToken);

    Task<Incident> MitigateAsync(Guid id, Guid actorId, string note, CancellationToken cancellationToken);

    Task<Incident> ResolveAsync(Guid id, Guid actorId, string? rootCause, CancellationToken cancellationToken);

    Task<Incident> ReopenAsync(Guid id, Guid actorId, string reason, CancellationToken cancellationToken);

    Task<Incident> AddNoteAsync(Guid id, Guid actorId, string message, CancellationToken cancellationToken);
}
