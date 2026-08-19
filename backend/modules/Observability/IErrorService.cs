using Observability.Domain;

namespace Observability;

/// <summary>
/// Reading and acting on grouped errors. The grouping itself happens through
/// <see cref="Knight.Application.Abstractions.ControlPlane.IErrorGrouping"/>,
/// which this same service implements — the write path is a port so ingestion
/// need not know this module exists, while the read path is a normal service
/// because the dashboard already does.
/// </summary>
public interface IErrorService
{
    Task<(IReadOnlyCollection<ErrorGroup> Items, long TotalCount)> ListGroupsAsync(
        Guid? storeId,
        ErrorGroupStatus? status,
        string? environment,
        int page,
        int pageSize,
        CancellationToken cancellationToken);

    Task<ErrorGroup> GetGroupAsync(Guid id, CancellationToken cancellationToken);

    Task<IReadOnlyCollection<ErrorGroupEventSample>> ListSamplesAsync(
        Guid groupId,
        int limit,
        CancellationToken cancellationToken);

    Task<ErrorGroup> AcknowledgeAsync(Guid id, Guid userId, CancellationToken cancellationToken);

    Task<ErrorGroup> ResolveAsync(Guid id, Guid userId, string? inVersion, CancellationToken cancellationToken);

    Task<ErrorGroup> IgnoreAsync(Guid id, Guid userId, CancellationToken cancellationToken);

    Task<ErrorGroup> ReopenAsync(Guid id, Guid userId, CancellationToken cancellationToken);
}
