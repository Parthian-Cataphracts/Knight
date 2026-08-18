namespace Ordering.Domain;

public sealed record OrderListFilter(
    OrderStatus? Status = null,
    long? OrderNumber = null,
    DateTimeOffset? CreatedFrom = null,
    DateTimeOffset? CreatedTo = null);

public interface IOrderRepository
{
    Task<Order?> GetByIdAsync(Guid tenantId, Guid orderId, CancellationToken cancellationToken);
    Task<Order?> GetByOrderNumberAsync(Guid tenantId, long orderNumber, CancellationToken cancellationToken);
    Task<(IReadOnlyCollection<Order> Items, long TotalCount)> ListAsync(
        Guid tenantId,
        int page,
        int pageSize,
        OrderListFilter filter,
        CancellationToken cancellationToken);
    Task AddAsync(Order order, CancellationToken cancellationToken);
    Task SaveChangesAsync(CancellationToken cancellationToken);
}
