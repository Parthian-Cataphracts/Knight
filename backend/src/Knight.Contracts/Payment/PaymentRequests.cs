namespace Knight.Contracts.Payment;

public sealed record CreatePaymentRequest(
    Guid OrderId,
    string Method);

public sealed record StartPaymentAttemptRequest(
    string? ProviderKey = null,
    string? ReturnUrl = null);

public sealed record CompletePaymentAttemptRequest(
    string OutcomeStatus,
    string? ProviderReference = null,
    string? FailureCode = null,
    string? FailureMessage = null);

public sealed record MarkPaymentPaidRequest(
    string? Reason = null);

public sealed record CancelPaymentRequest(
    string? Reason = null);
