namespace Knight.Contracts.Payment;

public sealed record PaymentResponse(
    Guid Id,
    Guid OrderId,
    decimal Amount,
    string Currency,
    string Method,
    string Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? SucceededAt,
    DateTimeOffset? FailedAt,
    DateTimeOffset? CancelledAt,
    IReadOnlyList<PaymentAttemptResponse> Attempts,
    IReadOnlyList<PaymentStatusHistoryResponse> StatusHistories);

public sealed record PaymentSummaryResponse(
    Guid Id,
    Guid OrderId,
    decimal Amount,
    string Currency,
    string Method,
    string Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? SucceededAt,
    DateTimeOffset? FailedAt,
    DateTimeOffset? CancelledAt);

public sealed record PaymentAttemptResponse(
    Guid Id,
    int AttemptNumber,
    string Status,
    string? ProviderKey,
    string? ProviderReference,
    DateTimeOffset CreatedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt,
    string? FailureCode,
    string? FailureMessage);

public sealed record PaymentStatusHistoryResponse(
    Guid Id,
    string FromStatus,
    string ToStatus,
    DateTimeOffset ChangedAt,
    string ActorType,
    Guid? ActorId,
    string? Reason);

public sealed record StartPaymentAttemptResponse(
    Guid AttemptId,
    int AttemptNumber,
    string Status,
    string? ProviderKey,
    string? ProviderReference,
    string? RedirectUrl);
