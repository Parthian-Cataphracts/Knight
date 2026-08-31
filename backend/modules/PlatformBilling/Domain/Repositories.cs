namespace PlatformBilling.Domain;

/// <summary>
/// Persistence for KNIGHT's own billing transactions. Customer-scoped by the
/// control-plane isolation filter, like every other customer-owned aggregate.
/// </summary>
public interface IPlatformBillingTransactionRepository
{
    Task<PlatformBillingTransaction?> GetByIdAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>
    /// The transaction for an idempotency key, if one exists. This is what makes
    /// a replayed webhook safe: the second delivery finds the first result rather
    /// than charging or activating again.
    /// </summary>
    Task<PlatformBillingTransaction?> FindByIdempotencyKeyAsync(string idempotencyKey, CancellationToken cancellationToken);

    Task<PlatformBillingTransaction?> FindByProviderTransactionAsync(
        string provider,
        string providerTransactionId,
        CancellationToken cancellationToken);

    Task AddAsync(PlatformBillingTransaction transaction, CancellationToken cancellationToken);

    Task SaveChangesAsync(CancellationToken cancellationToken);
}

/// <summary>Persistence for self-service checkout sessions.</summary>
public interface ICheckoutSessionRepository
{
    Task<CheckoutSession?> GetByIdAsync(Guid id, CancellationToken cancellationToken);

    Task<CheckoutSession?> FindByProviderSessionAsync(
        string provider,
        string providerSessionId,
        CancellationToken cancellationToken);

    Task AddAsync(CheckoutSession session, CancellationToken cancellationToken);

    Task SaveChangesAsync(CancellationToken cancellationToken);
}
