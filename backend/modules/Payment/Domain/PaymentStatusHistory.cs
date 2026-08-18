using Knight.Domain.Common;

namespace Payment.Domain;

public sealed class PaymentStatusHistory : Entity, ITenantScoped
{
    public const int MaxActorTypeLength = 50;
    public const int MaxReasonLength = 500;

    private PaymentStatusHistory()
    {
    }

    public Guid TenantId { get; private set; }

    public Guid PaymentId { get; private set; }

    public PaymentStatus FromStatus { get; private set; }

    public PaymentStatus ToStatus { get; private set; }

    public DateTimeOffset ChangedAt { get; private set; }

    public string ActorType { get; private set; } = null!;

    public Guid? ActorId { get; private set; }

    public string? Reason { get; private set; }

    public static PaymentStatusHistory Create(
        Guid id,
        Guid tenantId,
        Guid paymentId,
        PaymentStatus fromStatus,
        PaymentStatus toStatus,
        DateTimeOffset changedAt,
        string actorType,
        Guid? actorId,
        string? reason)
    {
        if (id == Guid.Empty) throw new ArgumentException("History ID cannot be empty.", nameof(id));
        if (tenantId == Guid.Empty) throw new ArgumentException("Tenant ID cannot be empty.", nameof(tenantId));
        if (paymentId == Guid.Empty) throw new ArgumentException("Payment ID cannot be empty.", nameof(paymentId));
        if (string.IsNullOrWhiteSpace(actorType)) throw new ArgumentException("Actor type cannot be empty.", nameof(actorType));

        return new PaymentStatusHistory
        {
            Id = id,
            TenantId = tenantId,
            PaymentId = paymentId,
            FromStatus = fromStatus,
            ToStatus = toStatus,
            ChangedAt = changedAt,
            ActorType = actorType.Trim(),
            ActorId = actorId,
            Reason = reason?.Trim()
        };
    }
}
