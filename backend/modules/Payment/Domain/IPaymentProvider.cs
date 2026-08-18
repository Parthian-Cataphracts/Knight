namespace Payment.Domain;

public sealed record PaymentProviderSessionRequest(
    Guid PaymentId,
    Guid AttemptId,
    int AttemptNumber,
    decimal Amount,
    string Currency,
    string? ReturnUrl);

public sealed record PaymentProviderSessionResult(
    string ProviderReference,
    string? RedirectUrl,
    IReadOnlyDictionary<string, string>? Metadata);

public interface IPaymentProvider
{
    string ProviderKey { get; }

    Task<PaymentProviderSessionResult> CreateSessionAsync(
        PaymentProviderSessionRequest request,
        CancellationToken cancellationToken);
}

public interface IPaymentProviderResolver
{
    IPaymentProvider? Resolve(string? providerKey);
}
