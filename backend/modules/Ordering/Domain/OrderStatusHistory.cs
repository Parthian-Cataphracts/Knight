using Knight.Application.Abstractions.Identity;
using Knight.Domain.Common;
using Knight.Domain.Exceptions;

namespace Ordering.Domain;

/// <summary>
/// Persistent business-visible history of transitions in an <see cref="Order"/>'s lifecycle.
/// </summary>
public sealed class OrderStatusHistory : Entity, ITenantScoped
{
    private const int MaxReasonLength = 500;

    public Guid TenantId { get; private set; }

    public Guid OrderId { get; private set; }

    public OrderStatus? FromStatus { get; private set; }

    public OrderStatus ToStatus { get; private set; }

    public DateTimeOffset ChangedAt { get; private set; }

    public Guid? ChangedByUserId { get; private set; }

    public PrincipalType? ChangedByPrincipalType { get; private set; }

    public string? Reason { get; private set; }

    private OrderStatusHistory()
    {
    }

    private OrderStatusHistory(
        Guid id,
        Guid tenantId,
        Guid orderId,
        OrderStatus? fromStatus,
        OrderStatus toStatus,
        DateTimeOffset changedAt,
        Guid? changedByUserId,
        PrincipalType? changedByPrincipalType,
        string? reason)
        : base(id)
    {
        TenantId = tenantId;
        OrderId = orderId;
        FromStatus = fromStatus;
        ToStatus = toStatus;
        ChangedAt = changedAt;
        ChangedByUserId = changedByUserId;
        ChangedByPrincipalType = changedByPrincipalType;
        Reason = reason;
    }

    public static OrderStatusHistory Create(
        Guid id,
        DateTimeOffset now,
        Guid tenantId,
        Guid orderId,
        OrderStatus? fromStatus,
        OrderStatus toStatus,
        Guid? changedByUserId = null,
        PrincipalType? changedByPrincipalType = null,
        string? reason = null)
    {
        if (tenantId == Guid.Empty)
        {
            throw DomainException.Validation("Tenant ID is required.");
        }

        if (orderId == Guid.Empty)
        {
            throw DomainException.Validation("Order ID is required.");
        }

        if (reason is not null && reason.Length > MaxReasonLength)
        {
            throw DomainException.Validation($"Reason cannot exceed {MaxReasonLength} characters.");
        }

        return new OrderStatusHistory(
            id,
            tenantId,
            orderId,
            fromStatus,
            toStatus,
            now,
            changedByUserId,
            changedByPrincipalType,
            reason?.Trim());
    }
}
