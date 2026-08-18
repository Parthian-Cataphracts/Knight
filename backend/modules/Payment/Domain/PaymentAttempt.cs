using Knight.Application.Exceptions;
using Knight.Domain.Common;

namespace Payment.Domain;

public sealed class PaymentAttempt : Entity, ITenantScoped
{
    public const int MaxProviderKeyLength = 50;
    public const int MaxProviderReferenceLength = 100;
    public const int MaxFailureCodeLength = 50;
    public const int MaxFailureMessageLength = 500;

    private PaymentAttempt()
    {
    }

    public Guid TenantId { get; private set; }

    public Guid PaymentId { get; private set; }

    public int AttemptNumber { get; private set; }

    public PaymentAttemptStatus Status { get; private set; }

    public string? ProviderKey { get; private set; }

    public string? ProviderReference { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset? StartedAt { get; private set; }

    public DateTimeOffset? CompletedAt { get; private set; }

    public string? FailureCode { get; private set; }

    public string? FailureMessage { get; private set; }

    public static PaymentAttempt Create(
        Guid id,
        Guid tenantId,
        Guid paymentId,
        int attemptNumber,
        string? providerKey,
        DateTimeOffset now)
    {
        if (id == Guid.Empty) throw new ArgumentException("Attempt ID cannot be empty.", nameof(id));
        if (tenantId == Guid.Empty) throw new ArgumentException("Tenant ID cannot be empty.", nameof(tenantId));
        if (paymentId == Guid.Empty) throw new ArgumentException("Payment ID cannot be empty.", nameof(paymentId));
        if (attemptNumber < 1) throw new ArgumentOutOfRangeException(nameof(attemptNumber), "Attempt number must be at least 1.");

        if (providerKey is not null && providerKey.Length > MaxProviderKeyLength)
        {
            throw new ValidationException(new Dictionary<string, string[]>
            {
                [nameof(providerKey)] = [$"Provider key cannot exceed {MaxProviderKeyLength} characters."]
            });
        }

        return new PaymentAttempt
        {
            Id = id,
            TenantId = tenantId,
            PaymentId = paymentId,
            AttemptNumber = attemptNumber,
            Status = PaymentAttemptStatus.Created,
            ProviderKey = providerKey?.Trim(),
            CreatedAt = now,
            StartedAt = now
        };
    }

    public void MarkProcessing(DateTimeOffset now)
    {
        if (Status is PaymentAttemptStatus.Succeeded or PaymentAttemptStatus.Failed or PaymentAttemptStatus.Cancelled)
        {
            throw new ConflictException($"Cannot transition attempt in state {Status} to Processing.");
        }

        Status = PaymentAttemptStatus.Processing;
    }

    public void MarkSucceeded(string? providerReference, DateTimeOffset now)
    {
        if (Status is PaymentAttemptStatus.Succeeded)
        {
            return; // Idempotent
        }

        if (Status is PaymentAttemptStatus.Failed or PaymentAttemptStatus.Cancelled)
        {
            throw new ConflictException($"Cannot transition attempt in terminal state {Status} to Succeeded.");
        }

        if (providerReference is not null && providerReference.Length > MaxProviderReferenceLength)
        {
            throw new ValidationException(new Dictionary<string, string[]>
            {
                [nameof(providerReference)] = [$"Provider reference cannot exceed {MaxProviderReferenceLength} characters."]
            });
        }

        Status = PaymentAttemptStatus.Succeeded;
        ProviderReference = providerReference?.Trim();
        CompletedAt = now;
    }

    public void MarkFailed(string? failureCode, string? failureMessage, string? providerReference, DateTimeOffset now)
    {
        if (Status is PaymentAttemptStatus.Failed)
        {
            return; // Idempotent
        }

        if (Status is PaymentAttemptStatus.Succeeded or PaymentAttemptStatus.Cancelled)
        {
            throw new ConflictException($"Cannot transition attempt in terminal state {Status} to Failed.");
        }

        if (failureCode is not null && failureCode.Length > MaxFailureCodeLength)
        {
            throw new ValidationException(new Dictionary<string, string[]>
            {
                [nameof(failureCode)] = [$"Failure code cannot exceed {MaxFailureCodeLength} characters."]
            });
        }

        if (failureMessage is not null && failureMessage.Length > MaxFailureMessageLength)
        {
            throw new ValidationException(new Dictionary<string, string[]>
            {
                [nameof(failureMessage)] = [$"Failure message cannot exceed {MaxFailureMessageLength} characters."]
            });
        }

        if (providerReference is not null && providerReference.Length > MaxProviderReferenceLength)
        {
            throw new ValidationException(new Dictionary<string, string[]>
            {
                [nameof(providerReference)] = [$"Provider reference cannot exceed {MaxProviderReferenceLength} characters."]
            });
        }

        Status = PaymentAttemptStatus.Failed;
        FailureCode = failureCode?.Trim();
        FailureMessage = failureMessage?.Trim();
        if (providerReference is not null)
        {
            ProviderReference = providerReference.Trim();
        }
        CompletedAt = now;
    }

    public void MarkCancelled(DateTimeOffset now)
    {
        if (Status is PaymentAttemptStatus.Cancelled)
        {
            return;
        }

        if (Status is PaymentAttemptStatus.Succeeded or PaymentAttemptStatus.Failed)
        {
            throw new ConflictException($"Cannot transition attempt in terminal state {Status} to Cancelled.");
        }

        Status = PaymentAttemptStatus.Cancelled;
        CompletedAt = now;
    }
}
