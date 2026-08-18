using Payment.Domain;
using Knight.Application.Abstractions.Identity;
using Knight.Application.Abstractions.Time;
using Knight.Application.Exceptions;
using Knight.Contracts.Common;
using Knight.Contracts.Payment;

namespace Payment;

public sealed class PaymentManagementService : IPaymentManagementService
{
    private readonly IPaymentRepository _paymentRepository;
    private readonly IPaymentOrderReader _orderReader;
    private readonly IPaymentProviderResolver _providerResolver;
    private readonly PaymentAuditRecorder _auditRecorder;
    private readonly ICurrentUser _currentUser;
    private readonly IDateTimeProvider _dateTimeProvider;

    public PaymentManagementService(
        IPaymentRepository paymentRepository,
        IPaymentOrderReader orderReader,
        IPaymentProviderResolver providerResolver,
        PaymentAuditRecorder auditRecorder,
        ICurrentUser currentUser,
        IDateTimeProvider dateTimeProvider)
    {
        _paymentRepository = paymentRepository;
        _orderReader = orderReader;
        _providerResolver = providerResolver;
        _auditRecorder = auditRecorder;
        _currentUser = currentUser;
        _dateTimeProvider = dateTimeProvider;
    }

    public async Task<PaymentResponse> CreatePaymentForOrderAsync(
        Guid tenantId,
        CreatePaymentRequest request,
        CancellationToken cancellationToken)
    {
        if (tenantId == Guid.Empty) throw new ArgumentException("Tenant ID cannot be empty.", nameof(tenantId));
        if (request.OrderId == Guid.Empty)
        {
            throw new ValidationException(new Dictionary<string, string[]>
            {
                [nameof(request.OrderId)] = ["Order ID is required."]
            });
        }

        if (!Enum.TryParse<PaymentMethod>(request.Method, ignoreCase: true, out var method))
        {
            throw new ValidationException(new Dictionary<string, string[]>
            {
                [nameof(request.Method)] = [$"Invalid payment method '{request.Method}'. Valid methods are: Online, PayOnFulfillment."]
            });
        }

        // 1. Check existing payment for order
        var existingPayment = await _paymentRepository.GetByOrderIdAsync(tenantId, request.OrderId, cancellationToken);
        if (existingPayment is not null)
        {
            throw new ConflictException(
                existingPayment.Status == PaymentStatus.Succeeded
                    ? "A succeeded payment already exists for this order."
                    : "A payment obligation already exists for this order. Use payment attempts to retry settlement.");
        }

        // 2. Read order snapshot server-side
        var orderSnapshot = await _orderReader.GetOrderSnapshotAsync(tenantId, request.OrderId, cancellationToken);
        if (orderSnapshot is null)
        {
            throw new NotFoundException($"Order '{request.OrderId}' was not found for the current tenant.");
        }

        if (orderSnapshot.IsCancelled || orderSnapshot.Status.Equals("Cancelled", StringComparison.OrdinalIgnoreCase))
        {
            throw new ValidationException(new Dictionary<string, string[]>
            {
                ["order"] = ["Cannot create payment for a cancelled order."]
            });
        }

        if (orderSnapshot.Total <= 0)
        {
            throw new ValidationException(new Dictionary<string, string[]>
            {
                ["order.total"] = ["Order total must be greater than zero to create a payment."]
            });
        }

        var now = _dateTimeProvider.UtcNow;
        var paymentId = Guid.NewGuid();

        var payment = Domain.Payment.Create(
            paymentId,
            tenantId,
            request.OrderId,
            orderSnapshot.Total,
            orderSnapshot.Currency,
            method,
            now);

        var actorTypeStr = _currentUser.PrincipalType == PrincipalType.TenantUser ? "TenantUser" : "System";
        var initialHistory = PaymentStatusHistory.Create(
            Guid.NewGuid(),
            tenantId,
            paymentId,
            PaymentStatus.Pending,
            PaymentStatus.Pending,
            now,
            actorTypeStr,
            _currentUser.UserId,
            "Payment obligation created from order total");

        return await _paymentRepository.ExecuteInTransactionAsync(async () =>
        {
            // Re-check inside transaction to handle concurrent creation race safely
            var concurrentExisting = await _paymentRepository.GetByOrderIdAsync(tenantId, request.OrderId, cancellationToken);
            if (concurrentExisting is not null)
            {
                throw new ConflictException("A payment obligation already exists for this order.");
            }

            await _paymentRepository.AddAsync(payment, cancellationToken);
            await _paymentRepository.AddStatusHistoryAsync(initialHistory, cancellationToken);
            await _paymentRepository.SaveChangesAsync(cancellationToken);

            await _auditRecorder.RecordAsync(
                "PaymentCreated",
                tenantId,
                "Payment",
                paymentId,
                cancellationToken,
                metadata: new Dictionary<string, string>
                {
                    ["OrderId"] = request.OrderId.ToString(),
                    ["Amount"] = payment.Amount.ToString("F2"),
                    ["Currency"] = payment.Currency,
                    ["Method"] = payment.Method.ToString()
                });

            return MapToResponse(payment, [initialHistory]);
        }, cancellationToken);
    }

    public async Task<StartPaymentAttemptResponse> StartAttemptAsync(
        Guid tenantId,
        Guid paymentId,
        StartPaymentAttemptRequest request,
        CancellationToken cancellationToken)
    {
        if (tenantId == Guid.Empty) throw new ArgumentException("Tenant ID cannot be empty.", nameof(tenantId));
        if (paymentId == Guid.Empty) throw new ArgumentException("Payment ID cannot be empty.", nameof(paymentId));

        var now = _dateTimeProvider.UtcNow;
        var attemptId = Guid.NewGuid();

        return await _paymentRepository.ExecuteInTransactionAsync(async () =>
        {
            var payment = await _paymentRepository.GetByIdForUpdateAsync(tenantId, paymentId, cancellationToken);
            if (payment is null)
            {
                throw new NotFoundException($"Payment '{paymentId}' was not found.");
            }

            if (payment.Method == PaymentMethod.PayOnFulfillment)
            {
                throw new ValidationException(new Dictionary<string, string[]>
                {
                    ["method"] = ["PayOnFulfillment payments do not support online payment attempts."]
                });
            }

            if (payment.Status is PaymentStatus.Succeeded)
            {
                throw new ConflictException("Cannot start an attempt for a succeeded payment.");
            }

            if (payment.Status is PaymentStatus.Cancelled)
            {
                throw new ConflictException("Cannot start an attempt for a cancelled payment.");
            }

            var attemptNumber = await _paymentRepository.GetNextAttemptNumberAsync(tenantId, paymentId, cancellationToken);
            var attempt = PaymentAttempt.Create(attemptId, tenantId, paymentId, attemptNumber, request.ProviderKey, now);
            attempt.MarkProcessing(now);

            string? redirectUrl = null;
            string? providerReference = null;

            var provider = _providerResolver.Resolve(request.ProviderKey);
            if (provider is not null)
            {
                var sessionRequest = new PaymentProviderSessionRequest(
                    paymentId,
                    attemptId,
                    attemptNumber,
                    payment.Amount,
                    payment.Currency,
                    request.ReturnUrl);

                var sessionResult = await provider.CreateSessionAsync(sessionRequest, cancellationToken);
                redirectUrl = sessionResult.RedirectUrl;
                providerReference = sessionResult.ProviderReference;
            }

            if (payment.Status == PaymentStatus.Pending)
            {
                var fromStatus = payment.Status;
                payment.TransitionToProcessing(now);
                var history = PaymentStatusHistory.Create(
                    Guid.NewGuid(),
                    tenantId,
                    paymentId,
                    fromStatus,
                    PaymentStatus.Processing,
                    now,
                    "System",
                    _currentUser.UserId,
                    "Started payment attempt");

                await _paymentRepository.AddStatusHistoryAsync(history, cancellationToken);
            }

            await _paymentRepository.AddAttemptAsync(attempt, cancellationToken);
            await _paymentRepository.SaveChangesAsync(cancellationToken);

            await _auditRecorder.RecordAsync(
                "PaymentAttemptStarted",
                tenantId,
                "PaymentAttempt",
                attemptId,
                cancellationToken,
                metadata: new Dictionary<string, string>
                {
                    ["PaymentId"] = paymentId.ToString(),
                    ["AttemptNumber"] = attemptNumber.ToString(),
                    ["ProviderKey"] = request.ProviderKey ?? "Default"
                });

            return new StartPaymentAttemptResponse(
                attempt.Id,
                attempt.AttemptNumber,
                attempt.Status.ToString(),
                attempt.ProviderKey,
                providerReference ?? attempt.ProviderReference,
                redirectUrl);
        }, cancellationToken);
    }

    public async Task<PaymentResponse> CompleteAttemptAsync(
        Guid tenantId,
        Guid paymentId,
        Guid attemptId,
        CompletePaymentAttemptRequest request,
        CancellationToken cancellationToken)
    {
        if (tenantId == Guid.Empty) throw new ArgumentException("Tenant ID cannot be empty.", nameof(tenantId));
        if (paymentId == Guid.Empty) throw new ArgumentException("Payment ID cannot be empty.", nameof(paymentId));
        if (attemptId == Guid.Empty) throw new ArgumentException("Attempt ID cannot be empty.", nameof(attemptId));

        if (!Enum.TryParse<PaymentAttemptStatus>(request.OutcomeStatus, ignoreCase: true, out var outcomeStatus) ||
            outcomeStatus == PaymentAttemptStatus.Created)
        {
            throw new ValidationException(new Dictionary<string, string[]>
            {
                [nameof(request.OutcomeStatus)] = [$"Invalid outcome status '{request.OutcomeStatus}'. Must be Succeeded, Failed, or Cancelled."]
            });
        }

        var now = _dateTimeProvider.UtcNow;

        return await _paymentRepository.ExecuteInTransactionAsync(async () =>
        {
            var payment = await _paymentRepository.GetByIdWithDetailsAsync(tenantId, paymentId, cancellationToken);
            if (payment is null)
            {
                throw new NotFoundException($"Payment '{paymentId}' was not found.");
            }

            var attempt = await _paymentRepository.GetAttemptByIdAsync(tenantId, paymentId, attemptId, cancellationToken);
            if (attempt is null)
            {
                throw new NotFoundException($"Payment attempt '{attemptId}' was not found.");
            }

            if (outcomeStatus == PaymentAttemptStatus.Succeeded)
            {
                if (payment.Status == PaymentStatus.Succeeded)
                {
                    if (attempt.Status == PaymentAttemptStatus.Succeeded)
                    {
                        return MapToResponse(payment); // Idempotent
                    }

                    throw new ConflictException("Another attempt has already succeeded for this payment.");
                }

                if (payment.Status is PaymentStatus.Cancelled or PaymentStatus.Failed)
                {
                    throw new ConflictException($"Cannot complete attempt for payment in terminal state {payment.Status}.");
                }

                attempt.MarkSucceeded(request.ProviderReference, now);

                var fromStatus = payment.Status;
                payment.TransitionToSucceeded(now);

                var history = PaymentStatusHistory.Create(
                    Guid.NewGuid(),
                    tenantId,
                    paymentId,
                    fromStatus,
                    PaymentStatus.Succeeded,
                    now,
                    "Provider",
                    null,
                    "Settlement succeeded via provider attempt");

                await _paymentRepository.AddStatusHistoryAsync(history, cancellationToken);
                await _paymentRepository.SaveChangesAsync(cancellationToken);

                await _auditRecorder.RecordAsync(
                    "PaymentSucceeded",
                    tenantId,
                    "Payment",
                    paymentId,
                    cancellationToken,
                    metadata: new Dictionary<string, string>
                    {
                        ["AttemptId"] = attemptId.ToString(),
                        ["ProviderReference"] = request.ProviderReference ?? string.Empty
                    });
            }
            else if (outcomeStatus == PaymentAttemptStatus.Failed)
            {
                attempt.MarkFailed(request.FailureCode, request.FailureMessage, request.ProviderReference, now);
                await _paymentRepository.SaveChangesAsync(cancellationToken);

                await _auditRecorder.RecordAsync(
                    "PaymentFailed",
                    tenantId,
                    "PaymentAttempt",
                    attemptId,
                    cancellationToken,
                    metadata: new Dictionary<string, string>
                    {
                        ["PaymentId"] = paymentId.ToString(),
                        ["FailureCode"] = request.FailureCode ?? string.Empty,
                        ["FailureMessage"] = request.FailureMessage ?? string.Empty
                    });
            }
            else if (outcomeStatus == PaymentAttemptStatus.Cancelled)
            {
                attempt.MarkCancelled(now);
                await _paymentRepository.SaveChangesAsync(cancellationToken);
            }

            return MapToResponse(payment);
        }, cancellationToken);
    }

    public async Task<PaymentResponse> MarkPaidAsync(
        Guid tenantId,
        Guid paymentId,
        MarkPaymentPaidRequest request,
        CancellationToken cancellationToken)
    {
        if (tenantId == Guid.Empty) throw new ArgumentException("Tenant ID cannot be empty.", nameof(tenantId));
        if (paymentId == Guid.Empty) throw new ArgumentException("Payment ID cannot be empty.", nameof(paymentId));

        var now = _dateTimeProvider.UtcNow;

        return await _paymentRepository.ExecuteInTransactionAsync(async () =>
        {
            var payment = await _paymentRepository.GetByIdWithDetailsAsync(tenantId, paymentId, cancellationToken);
            if (payment is null)
            {
                throw new NotFoundException($"Payment '{paymentId}' was not found.");
            }

            if (payment.Method == PaymentMethod.Online)
            {
                throw new ConflictException("Online payments cannot be manually marked as paid. Settlement must occur through provider processing.");
            }

            if (payment.Status == PaymentStatus.Succeeded)
            {
                return MapToResponse(payment); // Idempotent
            }

            if (payment.Status is PaymentStatus.Cancelled or PaymentStatus.Failed)
            {
                throw new ConflictException($"Cannot mark payment in terminal state {payment.Status} as paid.");
            }

            var fromStatus = payment.Status;
            payment.TransitionToSucceeded(now);

            var history = PaymentStatusHistory.Create(
                Guid.NewGuid(),
                tenantId,
                paymentId,
                fromStatus,
                PaymentStatus.Succeeded,
                now,
                "TenantUser",
                _currentUser.UserId,
                request.Reason ?? "Manually marked paid upon fulfillment");

            await _paymentRepository.AddStatusHistoryAsync(history, cancellationToken);
            await _paymentRepository.SaveChangesAsync(cancellationToken);

            await _auditRecorder.RecordAsync(
                "PaymentMarkedPaid",
                tenantId,
                "Payment",
                paymentId,
                cancellationToken,
                metadata: new Dictionary<string, string>
                {
                    ["OrderId"] = payment.OrderId.ToString(),
                    ["Amount"] = payment.Amount.ToString("F2"),
                    ["Reason"] = request.Reason ?? "Marked paid upon fulfillment"
                });

            return MapToResponse(payment);
        }, cancellationToken);
    }

    public async Task<PaymentResponse> CancelPaymentAsync(
        Guid tenantId,
        Guid paymentId,
        CancelPaymentRequest request,
        CancellationToken cancellationToken)
    {
        if (tenantId == Guid.Empty) throw new ArgumentException("Tenant ID cannot be empty.", nameof(tenantId));
        if (paymentId == Guid.Empty) throw new ArgumentException("Payment ID cannot be empty.", nameof(paymentId));

        var now = _dateTimeProvider.UtcNow;

        return await _paymentRepository.ExecuteInTransactionAsync(async () =>
        {
            var payment = await _paymentRepository.GetByIdWithDetailsAsync(tenantId, paymentId, cancellationToken);
            if (payment is null)
            {
                throw new NotFoundException($"Payment '{paymentId}' was not found.");
            }

            if (payment.Status == PaymentStatus.Cancelled)
            {
                return MapToResponse(payment); // Idempotent
            }

            if (payment.Status == PaymentStatus.Succeeded)
            {
                throw new ConflictException("Cannot cancel a succeeded payment.");
            }

            var fromStatus = payment.Status;
            payment.TransitionToCancelled(now);

            var history = PaymentStatusHistory.Create(
                Guid.NewGuid(),
                tenantId,
                paymentId,
                fromStatus,
                PaymentStatus.Cancelled,
                now,
                "TenantUser",
                _currentUser.UserId,
                request.Reason ?? "Payment cancelled");

            await _paymentRepository.AddStatusHistoryAsync(history, cancellationToken);
            await _paymentRepository.SaveChangesAsync(cancellationToken);

            await _auditRecorder.RecordAsync(
                "PaymentCancelled",
                tenantId,
                "Payment",
                paymentId,
                cancellationToken,
                metadata: new Dictionary<string, string>
                {
                    ["OrderId"] = payment.OrderId.ToString(),
                    ["Reason"] = request.Reason ?? "Payment cancelled"
                });

            return MapToResponse(payment);
        }, cancellationToken);
    }

    public async Task<PaymentResponse> GetPaymentByIdAsync(
        Guid tenantId,
        Guid paymentId,
        CancellationToken cancellationToken)
    {
        if (tenantId == Guid.Empty) throw new ArgumentException("Tenant ID cannot be empty.", nameof(tenantId));
        if (paymentId == Guid.Empty) throw new ArgumentException("Payment ID cannot be empty.", nameof(paymentId));

        var payment = await _paymentRepository.GetByIdWithDetailsAsync(tenantId, paymentId, cancellationToken);
        if (payment is null)
        {
            throw new NotFoundException($"Payment '{paymentId}' was not found.");
        }

        return MapToResponse(payment);
    }

    public async Task<PagedResponse<PaymentSummaryResponse>> ListPaymentsAsync(
        Guid tenantId,
        PaymentListFilter filter,
        CancellationToken cancellationToken)
    {
        if (tenantId == Guid.Empty) throw new ArgumentException("Tenant ID cannot be empty.", nameof(tenantId));

        var (items, totalCount) = await _paymentRepository.ListAsync(tenantId, filter, cancellationToken);

        var dtos = items.Select(p => new PaymentSummaryResponse(
            p.Id,
            p.OrderId,
            p.Amount,
            p.Currency,
            p.Method.ToString(),
            p.Status.ToString(),
            p.CreatedAt,
            p.UpdatedAt,
            p.SucceededAt,
            p.FailedAt,
            p.CancelledAt)).ToArray();

        return new PagedResponse<PaymentSummaryResponse>
        {
            Items = dtos,
            Page = filter.Page,
            PageSize = filter.PageSize,
            TotalCount = totalCount
        };
    }

    private static PaymentResponse MapToResponse(Domain.Payment payment, IReadOnlyList<PaymentStatusHistory>? explicitHistories = null)
    {
        var attempts = payment.Attempts
            .OrderBy(a => a.AttemptNumber)
            .Select(a => new PaymentAttemptResponse(
                a.Id,
                a.AttemptNumber,
                a.Status.ToString(),
                a.ProviderKey,
                a.ProviderReference,
                a.CreatedAt,
                a.StartedAt,
                a.CompletedAt,
                a.FailureCode,
                a.FailureMessage))
            .ToArray();

        var historiesSource = explicitHistories ?? payment.StatusHistories;
        var histories = historiesSource
            .OrderBy(h => h.ChangedAt)
            .Select(h => new PaymentStatusHistoryResponse(
                h.Id,
                h.FromStatus.ToString(),
                h.ToStatus.ToString(),
                h.ChangedAt,
                h.ActorType,
                h.ActorId,
                h.Reason))
            .ToArray();

        return new PaymentResponse(
            payment.Id,
            payment.OrderId,
            payment.Amount,
            payment.Currency,
            payment.Method.ToString(),
            payment.Status.ToString(),
            payment.CreatedAt,
            payment.UpdatedAt,
            payment.SucceededAt,
            payment.FailedAt,
            payment.CancelledAt,
            attempts,
            histories);
    }
}
