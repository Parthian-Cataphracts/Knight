namespace Payment.Domain;

public sealed record PaymentOrderSnapshot(
    Guid OrderId,
    Guid TenantId,
    decimal Total,
    string Currency,
    string Status,
    bool IsCancelled);

public interface IPaymentOrderReader
{
    Task<PaymentOrderSnapshot?> GetOrderSnapshotAsync(Guid tenantId, Guid orderId, CancellationToken cancellationToken);
}
