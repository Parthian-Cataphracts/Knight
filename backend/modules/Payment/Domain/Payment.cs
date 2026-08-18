using Knight.Application.Exceptions;
using Knight.Domain.Common;

namespace Payment.Domain;

public sealed class Payment : Entity, ITenantScoped
{
    public const int MaxCurrencyLength = 10;

    private readonly List<PaymentAttempt> _attempts = [];
    private readonly List<PaymentStatusHistory> _statusHistories = [];

    private Payment()
    {
    }

    public Guid TenantId { get; private set; }

    public Guid OrderId { get; private set; }

    public decimal Amount { get; private set; }

    public string Currency { get; private set; } = null!;

    public PaymentMethod Method { get; private set; }

    public PaymentStatus Status { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public DateTimeOffset? SucceededAt { get; private set; }

    public DateTimeOffset? FailedAt { get; private set; }

    public DateTimeOffset? CancelledAt { get; private set; }

    public int Version { get; private set; } = 1;

    public IReadOnlyCollection<PaymentAttempt> Attempts => _attempts.AsReadOnly();

    public IReadOnlyCollection<PaymentStatusHistory> StatusHistories => _statusHistories.AsReadOnly();

    public static Payment Create(
        Guid id,
        Guid tenantId,
        Guid orderId,
        decimal amount,
        string currency,
        PaymentMethod method,
        DateTimeOffset now)
    {
        if (id == Guid.Empty) throw new ArgumentException("Payment ID cannot be empty.", nameof(id));
        if (tenantId == Guid.Empty) throw new ArgumentException("Tenant ID cannot be empty.", nameof(tenantId));
        if (orderId == Guid.Empty) throw new ArgumentException("Order ID cannot be empty.", nameof(orderId));

        if (amount <= 0)
        {
            throw new ValidationException(new Dictionary<string, string[]>
            {
                [nameof(amount)] = ["Payment amount must be greater than zero."]
            });
        }

        if (string.IsNullOrWhiteSpace(currency))
        {
            throw new ValidationException(new Dictionary<string, string[]>
            {
                [nameof(currency)] = ["Payment currency is required."]
            });
        }

        var normalizedCurrency = currency.Trim().ToUpperInvariant();
        if (normalizedCurrency.Length > MaxCurrencyLength)
        {
            throw new ValidationException(new Dictionary<string, string[]>
            {
                [nameof(currency)] = [$"Payment currency cannot exceed {MaxCurrencyLength} characters."]
            });
        }

        return new Payment
        {
            Id = id,
            TenantId = tenantId,
            OrderId = orderId,
            Amount = amount,
            Currency = normalizedCurrency,
            Method = method,
            Status = PaymentStatus.Pending,
            CreatedAt = now,
            UpdatedAt = now
        };
    }

    public void TransitionToProcessing(DateTimeOffset now)
    {
        if (Status == PaymentStatus.Processing)
        {
            return;
        }

        if (Status != PaymentStatus.Pending)
        {
            throw new ConflictException($"Cannot transition payment in state {Status} to Processing.");
        }

        Status = PaymentStatus.Processing;
        Version++;
        UpdatedAt = now;
    }

    public void TransitionToSucceeded(DateTimeOffset now)
    {
        if (Status == PaymentStatus.Succeeded)
        {
            return;
        }

        if (Status is PaymentStatus.Failed or PaymentStatus.Cancelled)
        {
            throw new ConflictException($"Cannot transition payment in terminal state {Status} to Succeeded.");
        }

        Status = PaymentStatus.Succeeded;
        Version++;
        SucceededAt = now;
        UpdatedAt = now;
    }

    public void TransitionToFailed(DateTimeOffset now)
    {
        if (Status == PaymentStatus.Failed)
        {
            return;
        }

        if (Status is PaymentStatus.Succeeded or PaymentStatus.Cancelled)
        {
            throw new ConflictException($"Cannot transition payment in terminal state {Status} to Failed.");
        }

        Status = PaymentStatus.Failed;
        Version++;
        FailedAt = now;
        UpdatedAt = now;
    }

    public void TransitionToCancelled(DateTimeOffset now)
    {
        if (Status == PaymentStatus.Cancelled)
        {
            return;
        }

        if (Status is PaymentStatus.Succeeded or PaymentStatus.Failed)
        {
            throw new ConflictException($"Cannot cancel payment in terminal state {Status}.");
        }

        Status = PaymentStatus.Cancelled;
        Version++;
        CancelledAt = now;
        UpdatedAt = now;
    }
}
