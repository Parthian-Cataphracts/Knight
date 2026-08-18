using NSubstitute;
using Payment;
using Payment.Domain;
using Knight.Application.Abstractions.Auditing;
using Knight.Application.Abstractions.Identity;
using Knight.Application.Abstractions.Time;
using Knight.Application.Exceptions;
using Knight.Contracts.Payment;
using Xunit;
using PaymentEntity = Payment.Domain.Payment;

namespace Knight.UnitTests.Payment;

public sealed class PaymentManagementServiceTests
{
    private readonly IPaymentRepository _paymentRepository = Substitute.For<IPaymentRepository>();
    private readonly IPaymentOrderReader _orderReader = Substitute.For<IPaymentOrderReader>();
    private readonly IPaymentProviderResolver _providerResolver = Substitute.For<IPaymentProviderResolver>();
    private readonly IAuditLogger _auditLogger = Substitute.For<IAuditLogger>();
    private readonly ICurrentUser _currentUser = Substitute.For<ICurrentUser>();
    private readonly IDateTimeProvider _dateTimeProvider = Substitute.For<IDateTimeProvider>();
    private readonly PaymentManagementService _sut;

    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly Guid _orderId = Guid.NewGuid();
    private readonly DateTimeOffset _now = DateTimeOffset.UtcNow;

    public PaymentManagementServiceTests()
    {
        _dateTimeProvider.UtcNow.Returns(_now);
        _currentUser.UserId.Returns(Guid.NewGuid());
        _currentUser.PrincipalType.Returns(PrincipalType.TenantUser);

        _paymentRepository.ExecuteInTransactionAsync(Arg.Any<Func<Task<PaymentResponse>>>(), Arg.Any<CancellationToken>())
            .Returns(callInfo => callInfo.Arg<Func<Task<PaymentResponse>>>()());

        _paymentRepository.ExecuteInTransactionAsync(Arg.Any<Func<Task<StartPaymentAttemptResponse>>>(), Arg.Any<CancellationToken>())
            .Returns(callInfo => callInfo.Arg<Func<Task<StartPaymentAttemptResponse>>>()());

        var auditRecorder = new PaymentAuditRecorder(_currentUser, _auditLogger);

        _sut = new PaymentManagementService(
            _paymentRepository,
            _orderReader,
            _providerResolver,
            auditRecorder,
            _currentUser,
            _dateTimeProvider);
    }

    [Fact]
    public async Task CreatePayment_WhenOrderNotFound_ThrowsNotFoundException()
    {
        _paymentRepository.GetByOrderIdAsync(_tenantId, _orderId, Arg.Any<CancellationToken>())
            .Returns((PaymentEntity?)null);

        _orderReader.GetOrderSnapshotAsync(_tenantId, _orderId, Arg.Any<CancellationToken>())
            .Returns((PaymentOrderSnapshot?)null);

        var request = new CreatePaymentRequest(_orderId, "Online");

        await Assert.ThrowsAsync<NotFoundException>(() =>
            _sut.CreatePaymentForOrderAsync(_tenantId, request, CancellationToken.None));
    }

    [Fact]
    public async Task CreatePayment_WhenOrderCancelled_ThrowsValidationException()
    {
        _paymentRepository.GetByOrderIdAsync(_tenantId, _orderId, Arg.Any<CancellationToken>())
            .Returns((PaymentEntity?)null);

        _orderReader.GetOrderSnapshotAsync(_tenantId, _orderId, Arg.Any<CancellationToken>())
            .Returns(new PaymentOrderSnapshot(_orderId, _tenantId, 100m, "USD", "Cancelled", IsCancelled: true));

        var request = new CreatePaymentRequest(_orderId, "Online");

        var ex = await Assert.ThrowsAsync<ValidationException>(() =>
            _sut.CreatePaymentForOrderAsync(_tenantId, request, CancellationToken.None));

        Assert.Contains("order", ex.Errors.Keys, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CreatePayment_WhenPaymentAlreadyExists_ThrowsConflictException()
    {
        var existing = PaymentEntity.Create(Guid.NewGuid(), _tenantId, _orderId, 100m, "USD", PaymentMethod.Online, _now);
        _paymentRepository.GetByOrderIdAsync(_tenantId, _orderId, Arg.Any<CancellationToken>())
            .Returns(existing);

        var request = new CreatePaymentRequest(_orderId, "Online");

        await Assert.ThrowsAsync<ConflictException>(() =>
            _sut.CreatePaymentForOrderAsync(_tenantId, request, CancellationToken.None));
    }

    [Fact]
    public async Task CreatePayment_ValidInputs_CreatesAndReturnsPayment()
    {
        _paymentRepository.GetByOrderIdAsync(_tenantId, _orderId, Arg.Any<CancellationToken>())
            .Returns((PaymentEntity?)null);

        _orderReader.GetOrderSnapshotAsync(_tenantId, _orderId, Arg.Any<CancellationToken>())
            .Returns(new PaymentOrderSnapshot(_orderId, _tenantId, 85.50m, "EUR", "Placed", IsCancelled: false));

        var request = new CreatePaymentRequest(_orderId, "PayOnFulfillment");

        var result = await _sut.CreatePaymentForOrderAsync(_tenantId, request, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(_orderId, result.OrderId);
        Assert.Equal(85.50m, result.Amount);
        Assert.Equal("EUR", result.Currency);
        Assert.Equal("PayOnFulfillment", result.Method);
        Assert.Equal("Pending", result.Status);

        await _paymentRepository.Received(1).AddAsync(Arg.Any<PaymentEntity>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task MarkPaid_OnlinePayment_ThrowsConflictException()
    {
        var paymentId = Guid.NewGuid();
        var onlinePayment = PaymentEntity.Create(paymentId, _tenantId, _orderId, 100m, "USD", PaymentMethod.Online, _now);

        _paymentRepository.GetByIdWithDetailsAsync(_tenantId, paymentId, Arg.Any<CancellationToken>())
            .Returns(onlinePayment);

        await Assert.ThrowsAsync<ConflictException>(() =>
            _sut.MarkPaidAsync(_tenantId, paymentId, new MarkPaymentPaidRequest("manual test"), CancellationToken.None));
    }

    [Fact]
    public async Task MarkPaid_PayOnFulfillment_TransitionsToSucceeded()
    {
        var paymentId = Guid.NewGuid();
        var pofPayment = PaymentEntity.Create(paymentId, _tenantId, _orderId, 100m, "USD", PaymentMethod.PayOnFulfillment, _now);

        _paymentRepository.GetByIdWithDetailsAsync(_tenantId, paymentId, Arg.Any<CancellationToken>())
            .Returns(pofPayment);

        var result = await _sut.MarkPaidAsync(_tenantId, paymentId, new MarkPaymentPaidRequest("Collected cash on delivery"), CancellationToken.None);

        Assert.Equal("Succeeded", result.Status);
        Assert.NotNull(result.SucceededAt);
    }
}
