using Payment.Domain;
using Knight.Contracts.Common;
using Knight.Contracts.Payment;

namespace Payment;

public interface IPaymentManagementService
{
    Task<PaymentResponse> CreatePaymentForOrderAsync(
        Guid tenantId,
        CreatePaymentRequest request,
        CancellationToken cancellationToken);

    Task<StartPaymentAttemptResponse> StartAttemptAsync(
        Guid tenantId,
        Guid paymentId,
        StartPaymentAttemptRequest request,
        CancellationToken cancellationToken);

    Task<PaymentResponse> CompleteAttemptAsync(
        Guid tenantId,
        Guid paymentId,
        Guid attemptId,
        CompletePaymentAttemptRequest request,
        CancellationToken cancellationToken);

    Task<PaymentResponse> MarkPaidAsync(
        Guid tenantId,
        Guid paymentId,
        MarkPaymentPaidRequest request,
        CancellationToken cancellationToken);

    Task<PaymentResponse> CancelPaymentAsync(
        Guid tenantId,
        Guid paymentId,
        CancelPaymentRequest request,
        CancellationToken cancellationToken);

    Task<PaymentResponse> GetPaymentByIdAsync(
        Guid tenantId,
        Guid paymentId,
        CancellationToken cancellationToken);

    Task<PagedResponse<PaymentSummaryResponse>> ListPaymentsAsync(
        Guid tenantId,
        PaymentListFilter filter,
        CancellationToken cancellationToken);
}
