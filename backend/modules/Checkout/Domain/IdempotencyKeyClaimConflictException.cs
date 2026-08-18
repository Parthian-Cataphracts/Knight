using Knight.Application.Exceptions;

namespace Checkout.Domain;

/// <summary>
/// Raised by <see cref="ICheckoutIdempotencyRepository"/> implementations when a
/// concurrent request won the race to claim the same idempotency key — i.e. the
/// unique <c>(TenantId, KeyHash)</c> constraint rejected this request's claim.
///
/// This is a distinct type rather than a plain <see cref="ConflictException"/>
/// because <c>CheckoutService</c> retries on it: the winner is still committing its
/// order, and once it has, the loser can replay that order instead of failing. That
/// retry decision must key off the exception type, never off its message — see
/// docs/adr/0008-transactional-checkout-idempotency-and-quote-orchestration.md.
///
/// Inherits <see cref="ConflictException"/> so that, if every retry is exhausted,
/// the API boundary still maps it to 409 exactly as before.
/// </summary>
public sealed class IdempotencyKeyClaimConflictException : ConflictException
{
    public IdempotencyKeyClaimConflictException()
        : base("The idempotency key is currently being processed by another concurrent request.")
    {
    }
}
