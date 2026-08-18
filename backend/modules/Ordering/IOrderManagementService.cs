using Ordering.Domain;

namespace Ordering;

public sealed record OrderListResult(
    IReadOnlyCollection<Order> Items,
    long TotalCount,
    int Page,
    int PageSize);

public interface IOrderManagementService
{
    Task<Order?> GetByIdAsync(Guid tenantId, Guid orderId, CancellationToken cancellationToken);

    Task<OrderListResult> ListAsync(
        Guid tenantId,
        int page,
        int pageSize,
        OrderListFilter filter,
        CancellationToken cancellationToken);

    Task<Order> TransitionStatusAsync(
        Guid tenantId,
        Guid orderId,
        OrderStatus targetStatus,
        string? reason,
        CancellationToken cancellationToken);

    Task<Order> CancelAsync(
        Guid tenantId,
        Guid orderId,
        string? reason,
        CancellationToken cancellationToken);
}
