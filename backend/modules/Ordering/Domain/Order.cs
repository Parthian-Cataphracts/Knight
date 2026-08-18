using Knight.Application.Abstractions.Identity;
using Knight.Domain.Common;
using Knight.Domain.Exceptions;

namespace Ordering.Domain;

/// <summary>
/// Root aggregate representing a tenant-scoped order. Manages historical snapshots
/// of order line items and controls the explicit lifecycle state machine.
/// </summary>
public sealed class Order : AuditableEntity, ITenantScoped
{
    private const int MaxItemsLimit = 100;
    private const int CurrencyLength = 3;
    private const int MaxCancellationReasonLength = 500;

    public Guid TenantId { get; private set; }

    public long OrderNumber { get; private set; }

    public OrderStatus Status { get; private set; }

    public string Currency { get; private set; }

    public decimal Subtotal { get; private set; }

    public decimal DiscountTotal { get; private set; }

    public decimal DiscountedSubtotal => Math.Max(0m, Subtotal - DiscountTotal);

    public decimal FulfillmentFee { get; private set; }

    public decimal Total { get; private set; }

    public DateTimeOffset? CompletedAt { get; private set; }

    public DateTimeOffset? CancelledAt { get; private set; }

    public string? CancellationReason { get; private set; }

    /// <summary>Optimistic concurrency version token.</summary>
    public int Version { get; private set; } = 1;

    public OrderPartySnapshot? Party { get; private set; }

    public OrderFulfillmentSnapshot? Fulfillment { get; private set; }

    public OrderPromotionSnapshot? Promotion { get; private set; }

    private readonly List<OrderItem> _items = [];

    public IReadOnlyCollection<OrderItem> Items => _items.AsReadOnly();

    private readonly List<OrderStatusHistory> _statusHistory = [];

    public IReadOnlyCollection<OrderStatusHistory> StatusHistory => _statusHistory.AsReadOnly();

    private Order()
    {
        Currency = "USD";
    }

    private Order(
        Guid id,
        DateTimeOffset createdAt,
        Guid tenantId,
        long orderNumber,
        string currency,
        decimal subtotal,
        decimal discountTotal,
        decimal fulfillmentFee,
        decimal total,
        IEnumerable<OrderItem> items,
        OrderPartySnapshot? party,
        OrderFulfillmentSnapshot? fulfillment,
        OrderPromotionSnapshot? promotion)
        : base(id, createdAt)
    {
        TenantId = tenantId;
        OrderNumber = orderNumber;
        Status = OrderStatus.Pending;
        Currency = currency;
        Subtotal = subtotal;
        DiscountTotal = discountTotal;
        FulfillmentFee = fulfillmentFee;
        Total = total;
        Party = party;
        Fulfillment = fulfillment;
        Promotion = promotion;
        _items.AddRange(items);
    }

    public static Order Create(
        Guid id,
        DateTimeOffset now,
        Guid tenantId,
        long orderNumber,
        string currency,
        IReadOnlyCollection<OrderItem> items,
        Guid? actorUserId = null,
        PrincipalType? actorPrincipalType = null,
        OrderPartySnapshot? party = null,
        OrderFulfillmentSnapshot? fulfillment = null,
        decimal discountTotal = 0m,
        OrderPromotionSnapshot? promotionSnapshot = null)
    {
        if (tenantId == Guid.Empty)
        {
            throw DomainException.Validation("Tenant ID is required.");
        }

        if (orderNumber <= 0)
        {
            throw DomainException.Validation("Order number must be positive.");
        }

        if (string.IsNullOrWhiteSpace(currency) || currency.Trim().Length != CurrencyLength)
        {
            throw DomainException.Validation($"Currency must be a {CurrencyLength}-character ISO code.");
        }

        if (items is null || items.Count == 0)
        {
            throw DomainException.Validation("An order must contain at least one item.");
        }

        if (items.Count > MaxItemsLimit)
        {
            throw DomainException.Validation($"An order cannot exceed {MaxItemsLimit} items.");
        }

        var normalizedCurrency = currency.Trim().ToUpperInvariant();
        var subtotal = items.Sum(i => i.LineTotal);

        if (discountTotal < 0m)
        {
            throw DomainException.Validation("Discount total cannot be negative.");
        }

        if (discountTotal > subtotal)
        {
            throw DomainException.Validation("Discount total cannot exceed subtotal.");
        }

        var fulfillmentFee = fulfillment?.FulfillmentFee ?? 0m;
        if (fulfillmentFee < 0)
        {
            throw DomainException.Validation("Fulfillment fee cannot be negative.");
        }

        var discountedSubtotal = subtotal - discountTotal;
        var total = discountedSubtotal + fulfillmentFee;

        var order = new Order(
            id,
            now,
            tenantId,
            orderNumber,
            normalizedCurrency,
            subtotal,
            discountTotal,
            fulfillmentFee,
            total,
            items,
            party,
            fulfillment,
            promotionSnapshot);

        var initialHistory = OrderStatusHistory.Create(
            Guid.NewGuid(),
            now,
            tenantId,
            id,
            fromStatus: null,
            toStatus: OrderStatus.Pending,
            changedByUserId: actorUserId,
            changedByPrincipalType: actorPrincipalType,
            reason: null);

        order._statusHistory.Add(initialHistory);

        return order;
    }

    public void Confirm(DateTimeOffset now, Guid? actorUserId = null, PrincipalType? actorPrincipalType = null)
    {
        if (Status != OrderStatus.Pending)
        {
            throw DomainException.Conflict($"Cannot confirm an order with status '{Status}'. Only Pending orders can be confirmed.");
        }

        ApplyTransition(OrderStatus.Confirmed, now, actorUserId, actorPrincipalType, reason: null);
    }

    public void Prepare(DateTimeOffset now, Guid? actorUserId = null, PrincipalType? actorPrincipalType = null)
    {
        if (Status != OrderStatus.Confirmed)
        {
            throw DomainException.Conflict($"Cannot prepare an order with status '{Status}'. Only Confirmed orders can transition to Preparing.");
        }

        ApplyTransition(OrderStatus.Preparing, now, actorUserId, actorPrincipalType, reason: null);
    }

    public void Ready(DateTimeOffset now, Guid? actorUserId = null, PrincipalType? actorPrincipalType = null)
    {
        if (Status != OrderStatus.Preparing)
        {
            throw DomainException.Conflict($"Cannot mark an order as ready with status '{Status}'. Only Preparing orders can transition to Ready.");
        }

        ApplyTransition(OrderStatus.Ready, now, actorUserId, actorPrincipalType, reason: null);
    }

    public void Complete(DateTimeOffset now, Guid? actorUserId = null, PrincipalType? actorPrincipalType = null)
    {
        if (Status != OrderStatus.Ready)
        {
            throw DomainException.Conflict($"Cannot complete an order with status '{Status}'. Only Ready orders can be completed.");
        }

        CompletedAt = now;
        ApplyTransition(OrderStatus.Completed, now, actorUserId, actorPrincipalType, reason: null);
    }

    public void Cancel(DateTimeOffset now, Guid? actorUserId = null, PrincipalType? actorPrincipalType = null, string? reason = null)
    {
        if (Status is OrderStatus.Completed or OrderStatus.Cancelled)
        {
            throw DomainException.Conflict($"Cannot cancel an order with status '{Status}'. Terminal states cannot be changed.");
        }

        if (reason is not null && reason.Length > MaxCancellationReasonLength)
        {
            throw DomainException.Validation($"Cancellation reason cannot exceed {MaxCancellationReasonLength} characters.");
        }

        CancelledAt = now;
        CancellationReason = reason?.Trim();
        ApplyTransition(OrderStatus.Cancelled, now, actorUserId, actorPrincipalType, reason?.Trim());
    }

    public void TransitionTo(
        OrderStatus targetStatus,
        DateTimeOffset now,
        Guid? actorUserId = null,
        PrincipalType? actorPrincipalType = null,
        string? reason = null)
    {
        switch (targetStatus)
        {
            case OrderStatus.Confirmed:
                Confirm(now, actorUserId, actorPrincipalType);
                break;
            case OrderStatus.Preparing:
                Prepare(now, actorUserId, actorPrincipalType);
                break;
            case OrderStatus.Ready:
                Ready(now, actorUserId, actorPrincipalType);
                break;
            case OrderStatus.Completed:
                Complete(now, actorUserId, actorPrincipalType);
                break;
            case OrderStatus.Cancelled:
                Cancel(now, actorUserId, actorPrincipalType, reason);
                break;
            default:
                throw DomainException.Conflict($"Transition to '{targetStatus}' from '{Status}' is not allowed.");
        }
    }

    private void ApplyTransition(
        OrderStatus newStatus,
        DateTimeOffset now,
        Guid? actorUserId,
        PrincipalType? actorPrincipalType,
        string? reason)
    {
        var previousStatus = Status;
        Status = newStatus;
        Version++;
        MarkUpdated(now);

        var history = OrderStatusHistory.Create(
            Guid.NewGuid(),
            now,
            TenantId,
            Id,
            fromStatus: previousStatus,
            toStatus: newStatus,
            changedByUserId: actorUserId,
            changedByPrincipalType: actorPrincipalType,
            reason: reason);

        _statusHistory.Add(history);
    }
}
