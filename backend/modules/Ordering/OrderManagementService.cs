using Ordering.Domain;
using Knight.Application.Abstractions.Identity;
using Knight.Application.Abstractions.Time;
using Knight.Application.Exceptions;

namespace Ordering;

public sealed class OrderManagementService : IOrderManagementService
{
    private const string EntityType = nameof(Order);
    private const int MaxPageSize = 100;

    private readonly IOrderRepository _orderRepository;
    private readonly IDateTimeProvider _dateTimeProvider;
    private readonly ICurrentUser _currentUser;
    private readonly OrderingAuditRecorder _audit;

    public OrderManagementService(
        IOrderRepository orderRepository,
        IDateTimeProvider dateTimeProvider,
        ICurrentUser currentUser,
        OrderingAuditRecorder audit)
    {
        _orderRepository = orderRepository;
        _dateTimeProvider = dateTimeProvider;
        _currentUser = currentUser;
        _audit = audit;
    }

    public Task<Order?> GetByIdAsync(Guid tenantId, Guid orderId, CancellationToken cancellationToken) =>
        _orderRepository.GetByIdAsync(tenantId, orderId, cancellationToken);

    public async Task<OrderListResult> ListAsync(
        Guid tenantId,
        int page,
        int pageSize,
        OrderListFilter filter,
        CancellationToken cancellationToken)
    {
        var boundedPage = Math.Max(page, 1);
        var boundedPageSize = Math.Clamp(pageSize, 1, MaxPageSize);

        var (items, totalCount) = await _orderRepository.ListAsync(
            tenantId,
            boundedPage,
            boundedPageSize,
            filter,
            cancellationToken);

        return new OrderListResult(items, totalCount, boundedPage, boundedPageSize);
    }

    public async Task<Order> TransitionStatusAsync(
        Guid tenantId,
        Guid orderId,
        OrderStatus targetStatus,
        string? reason,
        CancellationToken cancellationToken)
    {
        var order = await _orderRepository.GetByIdAsync(tenantId, orderId, cancellationToken)
            ?? throw new NotFoundException(nameof(Order), orderId);

        var now = _dateTimeProvider.UtcNow;
        order.TransitionTo(targetStatus, now, _currentUser.UserId, _currentUser.PrincipalType, reason);

        await _orderRepository.SaveChangesAsync(cancellationToken);

        await _audit.RecordAsync(
            $"Order{targetStatus}",
            tenantId,
            EntityType,
            order.Id,
            cancellationToken,
            _currentUser.UserId,
            _currentUser.PrincipalType,
            new Dictionary<string, string>
            {
                ["orderNumber"] = order.OrderNumber.ToString(),
                ["newStatus"] = targetStatus.ToString()
            });

        return order;
    }

    public async Task<Order> CancelAsync(
        Guid tenantId,
        Guid orderId,
        string? reason,
        CancellationToken cancellationToken)
    {
        var order = await _orderRepository.GetByIdAsync(tenantId, orderId, cancellationToken)
            ?? throw new NotFoundException(nameof(Order), orderId);

        var now = _dateTimeProvider.UtcNow;
        order.Cancel(now, _currentUser.UserId, _currentUser.PrincipalType, reason);

        await _orderRepository.SaveChangesAsync(cancellationToken);

        await _audit.RecordAsync(
            "OrderCancelled",
            tenantId,
            EntityType,
            order.Id,
            cancellationToken,
            _currentUser.UserId,
            _currentUser.PrincipalType,
            new Dictionary<string, string>
            {
                ["orderNumber"] = order.OrderNumber.ToString(),
                ["reason"] = reason ?? string.Empty
            });

        return order;
    }
}
