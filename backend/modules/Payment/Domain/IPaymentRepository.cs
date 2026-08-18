namespace Payment.Domain;

public sealed record PaymentListFilter(
    PaymentStatus? Status = null,
    PaymentMethod? Method = null,
    Guid? OrderId = null,
    DateTimeOffset? FromDate = null,
    DateTimeOffset? ToDate = null,
    int Page = 1,
    int PageSize = 20);

public interface IPaymentRepository
{
    Task<Payment?> GetByIdAsync(Guid tenantId, Guid paymentId, CancellationToken cancellationToken);

    Task<Payment?> GetByIdWithDetailsAsync(Guid tenantId, Guid paymentId, CancellationToken cancellationToken);

    Task<Payment?> GetByIdForUpdateAsync(Guid tenantId, Guid paymentId, CancellationToken cancellationToken);

    Task<Payment?> GetByOrderIdAsync(Guid tenantId, Guid orderId, CancellationToken cancellationToken);

    Task<PaymentAttempt?> GetAttemptByIdAsync(Guid tenantId, Guid paymentId, Guid attemptId, CancellationToken cancellationToken);

    Task AddAsync(Payment payment, CancellationToken cancellationToken);

    Task AddAttemptAsync(PaymentAttempt attempt, CancellationToken cancellationToken);

    Task AddStatusHistoryAsync(PaymentStatusHistory history, CancellationToken cancellationToken);

    Task<int> GetNextAttemptNumberAsync(Guid tenantId, Guid paymentId, CancellationToken cancellationToken);

    Task<bool> HasActivePaymentForOrderAsync(Guid tenantId, Guid orderId, CancellationToken cancellationToken);

    Task<(IReadOnlyList<Payment> Items, int TotalCount)> ListAsync(Guid tenantId, PaymentListFilter filter, CancellationToken cancellationToken);

    Task SaveChangesAsync(CancellationToken cancellationToken);

    Task<TResult> ExecuteInTransactionAsync<TResult>(Func<Task<TResult>> operation, CancellationToken cancellationToken);
}
