using Knight.Domain;
using Knight.Domain.Common;
using Knight.Domain.Exceptions;

namespace Checkout.Domain;

public sealed class CheckoutIdempotencyRecord : Entity, ITenantScoped
{
    public const int MaxHashLength = 64;

    private CheckoutIdempotencyRecord()
    {
        KeyHash = null!;
        RequestHash = null!;
    }

    private CheckoutIdempotencyRecord(
        Guid id,
        Guid tenantId,
        string keyHash,
        string requestHash,
        DateTimeOffset createdAt) : base(id)
    {
        TenantId = tenantId;
        KeyHash = keyHash;
        RequestHash = requestHash;
        CreatedAt = createdAt;
    }

    public Guid TenantId { get; private set; }

    public string KeyHash { get; private set; }

    public string RequestHash { get; private set; }

    public Guid? OrderId { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset? CompletedAt { get; private set; }

    public bool IsCompleted => CompletedAt.HasValue && OrderId.HasValue;

    public static CheckoutIdempotencyRecord CreateClaim(
        Guid id,
        Guid tenantId,
        string keyHash,
        string requestHash,
        DateTimeOffset now)
    {
        if (id == Guid.Empty)
        {
            throw DomainException.Validation("Idempotency record ID cannot be empty.");
        }

        if (tenantId == Guid.Empty)
        {
            throw DomainException.Validation("Tenant ID cannot be empty.");
        }

        if (string.IsNullOrWhiteSpace(keyHash) || keyHash.Length > MaxHashLength)
        {
            throw DomainException.Validation("Key hash must be a valid non-empty hash.");
        }

        if (string.IsNullOrWhiteSpace(requestHash) || requestHash.Length > MaxHashLength)
        {
            throw DomainException.Validation("Request hash must be a valid non-empty hash.");
        }

        return new CheckoutIdempotencyRecord(id, tenantId, keyHash, requestHash, now);
    }

    public void Complete(Guid orderId, DateTimeOffset completedAt)
    {
        if (orderId == Guid.Empty)
        {
            throw DomainException.Validation("Order ID cannot be empty.");
        }

        OrderId = orderId;
        CompletedAt = completedAt;
    }
}
