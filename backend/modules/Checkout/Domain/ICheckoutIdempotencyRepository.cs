namespace Checkout.Domain;

public interface ICheckoutIdempotencyRepository
{
    Task<CheckoutIdempotencyRecord?> GetByKeyHashAsync(Guid tenantId, string keyHash, CancellationToken cancellationToken);

    Task AddAsync(CheckoutIdempotencyRecord record, CancellationToken cancellationToken);

    Task SaveChangesAsync(CancellationToken cancellationToken);

    Task<TResult> ExecuteInTransactionAsync<TResult>(
        Func<Task<TResult>> operation,
        CancellationToken cancellationToken);
}
