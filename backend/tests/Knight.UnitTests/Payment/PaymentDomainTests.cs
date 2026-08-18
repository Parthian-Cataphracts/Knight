using Payment.Domain;
using Knight.Application.Exceptions;
using Xunit;
using PaymentEntity = Payment.Domain.Payment;

namespace Knight.UnitTests.Payment;

public sealed class PaymentDomainTests
{
    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly Guid _orderId = Guid.NewGuid();
    private readonly DateTimeOffset _now = DateTimeOffset.UtcNow;

    [Fact]
    public void CreatePayment_ValidInputs_InitializesPendingPayment()
    {
        var paymentId = Guid.NewGuid();
        var payment = PaymentEntity.Create(paymentId, _tenantId, _orderId, 150.50m, "USD", PaymentMethod.Online, _now);

        Assert.Equal(paymentId, payment.Id);
        Assert.Equal(_tenantId, payment.TenantId);
        Assert.Equal(_orderId, payment.OrderId);
        Assert.Equal(150.50m, payment.Amount);
        Assert.Equal("USD", payment.Currency);
        Assert.Equal(PaymentMethod.Online, payment.Method);
        Assert.Equal(PaymentStatus.Pending, payment.Status);
        Assert.Equal(_now, payment.CreatedAt);
        Assert.Null(payment.SucceededAt);
        Assert.Null(payment.FailedAt);
        Assert.Null(payment.CancelledAt);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-10.5)]
    public void CreatePayment_NonPositiveAmount_ThrowsValidationException(decimal invalidAmount)
    {
        var ex = Assert.Throws<ValidationException>(() =>
            PaymentEntity.Create(Guid.NewGuid(), _tenantId, _orderId, invalidAmount, "USD", PaymentMethod.Online, _now));

        Assert.Contains("amount", ex.Errors.Keys, StringComparer.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void CreatePayment_MissingCurrency_ThrowsValidationException(string? invalidCurrency)
    {
        var ex = Assert.Throws<ValidationException>(() =>
            PaymentEntity.Create(Guid.NewGuid(), _tenantId, _orderId, 50.00m, invalidCurrency!, PaymentMethod.Online, _now));

        Assert.Contains("currency", ex.Errors.Keys, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void TransitionToProcessing_FromPending_Succeeds()
    {
        var payment = PaymentEntity.Create(Guid.NewGuid(), _tenantId, _orderId, 50.00m, "USD", PaymentMethod.Online, _now);
        payment.TransitionToProcessing(_now.AddMinutes(1));

        Assert.Equal(PaymentStatus.Processing, payment.Status);
    }

    [Fact]
    public void TransitionToSucceeded_FromProcessing_SetsSucceededAt()
    {
        var payment = PaymentEntity.Create(Guid.NewGuid(), _tenantId, _orderId, 50.00m, "USD", PaymentMethod.Online, _now);
        payment.TransitionToProcessing(_now.AddMinutes(1));
        var completedAt = _now.AddMinutes(2);
        payment.TransitionToSucceeded(completedAt);

        Assert.Equal(PaymentStatus.Succeeded, payment.Status);
        Assert.Equal(completedAt, payment.SucceededAt);
    }

    [Fact]
    public void TransitionToFailed_FromProcessing_SetsFailedAt()
    {
        var payment = PaymentEntity.Create(Guid.NewGuid(), _tenantId, _orderId, 50.00m, "USD", PaymentMethod.Online, _now);
        payment.TransitionToProcessing(_now.AddMinutes(1));
        var failedAt = _now.AddMinutes(2);
        payment.TransitionToFailed(failedAt);

        Assert.Equal(PaymentStatus.Failed, payment.Status);
        Assert.Equal(failedAt, payment.FailedAt);
    }

    [Fact]
    public void TransitionToCancelled_FromPending_SetsCancelledAt()
    {
        var payment = PaymentEntity.Create(Guid.NewGuid(), _tenantId, _orderId, 50.00m, "USD", PaymentMethod.Online, _now);
        var cancelledAt = _now.AddMinutes(1);
        payment.TransitionToCancelled(cancelledAt);

        Assert.Equal(PaymentStatus.Cancelled, payment.Status);
        Assert.Equal(cancelledAt, payment.CancelledAt);
    }

    [Fact]
    public void TransitionToSucceeded_FromTerminalCancelledOrFailed_ThrowsConflictException()
    {
        var payment = PaymentEntity.Create(Guid.NewGuid(), _tenantId, _orderId, 50.00m, "USD", PaymentMethod.Online, _now);
        payment.TransitionToCancelled(_now.AddMinutes(1));

        Assert.Throws<ConflictException>(() => payment.TransitionToSucceeded(_now.AddMinutes(2)));
    }

    [Fact]
    public void PaymentAttempt_LifecycleTransitions_WorkCorrectly()
    {
        var paymentId = Guid.NewGuid();
        var attemptId = Guid.NewGuid();
        var attempt = PaymentAttempt.Create(attemptId, _tenantId, paymentId, 1, "test-provider", _now);

        Assert.Equal(PaymentAttemptStatus.Created, attempt.Status);
        Assert.Equal(1, attempt.AttemptNumber);
        Assert.Equal("test-provider", attempt.ProviderKey);

        attempt.MarkProcessing(_now.AddSeconds(5));
        Assert.Equal(PaymentAttemptStatus.Processing, attempt.Status);

        attempt.MarkFailed("CARD_DECLINED", "Card has insufficient funds", "ref-123", _now.AddSeconds(10));
        Assert.Equal(PaymentAttemptStatus.Failed, attempt.Status);
        Assert.Equal("CARD_DECLINED", attempt.FailureCode);
        Assert.Equal("Card has insufficient funds", attempt.FailureMessage);
        Assert.Equal("ref-123", attempt.ProviderReference);
    }

    [Fact]
    public void PaymentAttempt_ExceedingProviderReferenceLength_ThrowsValidationException()
    {
        var attempt = PaymentAttempt.Create(Guid.NewGuid(), _tenantId, Guid.NewGuid(), 1, "test-provider", _now);
        var longRef = new string('x', PaymentAttempt.MaxProviderReferenceLength + 1);

        var ex = Assert.Throws<ValidationException>(() => attempt.MarkSucceeded(longRef, _now));
        Assert.Contains("providerReference", ex.Errors.Keys, StringComparer.OrdinalIgnoreCase);
    }
}
