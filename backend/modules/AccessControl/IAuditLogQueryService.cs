using AccessControl.Domain;

namespace AccessControl;

public sealed record AuditLogEntryView(
    Guid Id,
    string ActorType,
    Guid? ActorUserId,
    string? ActorDisplay,
    Guid? CustomerId,
    string Action,
    string TargetType,
    string? TargetId,
    string? PreviousValue,
    string? NewValue,
    string? CorrelationId,
    string? IpAddress,
    DateTimeOffset OccurredAt);

public sealed record PagedResult<T>(IReadOnlyCollection<T> Items, int Page, int PageSize, long TotalCount);

/// <summary>
/// Read access to the audit trail. There is deliberately no write method here:
/// entries are produced by <see cref="Knight.Application.Abstractions.ControlPlane.IAuditTrail"/> as a side
/// effect of the action being audited, never by a caller choosing what to record.
/// </summary>
public interface IAuditLogQueryService
{
    Task<PagedResult<AuditLogEntryView>> QueryAsync(AuditLogQuery query, CancellationToken cancellationToken);
}

internal sealed class AuditLogQueryService : IAuditLogQueryService
{
    private const int MaxPageSize = 200;

    private readonly IAuditLogRepository _repository;

    public AuditLogQueryService(IAuditLogRepository repository)
    {
        _repository = repository;
    }

    public async Task<PagedResult<AuditLogEntryView>> QueryAsync(AuditLogQuery query, CancellationToken cancellationToken)
    {
        var normalized = query with
        {
            Page = query.Page < 1 ? 1 : query.Page,
            PageSize = query.PageSize is < 1 or > MaxPageSize ? 25 : query.PageSize,
        };

        var (items, total) = await _repository.QueryAsync(normalized, cancellationToken);

        return new PagedResult<AuditLogEntryView>(
            items.Select(Map).ToArray(),
            normalized.Page,
            normalized.PageSize,
            total);
    }

    private static AuditLogEntryView Map(AuditLog entry) => new(
        entry.Id,
        entry.ActorType.ToString(),
        entry.ActorUserId,
        entry.ActorDisplay,
        entry.CustomerId,
        entry.Action,
        entry.TargetType,
        entry.TargetId,
        entry.PreviousValue,
        entry.NewValue,
        entry.CorrelationId,
        entry.IpAddress,
        entry.OccurredAt);
}
