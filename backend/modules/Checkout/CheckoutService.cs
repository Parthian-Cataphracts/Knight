using Checkout.Domain;
using Knight.Application.Abstractions.Time;
using Knight.Application.Exceptions;

namespace Checkout;

public sealed class CheckoutService : ICheckoutService
{
    private const int MaxLineItems = 100;

    private readonly ICheckoutIdempotencyRepository _idempotencyRepository;
    private readonly ICheckoutOrderingGateway _orderingGateway;
    private readonly ICheckoutRequestHasher _requestHasher;
    private readonly IDateTimeProvider _dateTimeProvider;

    public CheckoutService(
        ICheckoutIdempotencyRepository idempotencyRepository,
        ICheckoutOrderingGateway orderingGateway,
        ICheckoutRequestHasher requestHasher,
        IDateTimeProvider dateTimeProvider)
    {
        _idempotencyRepository = idempotencyRepository;
        _orderingGateway = orderingGateway;
        _requestHasher = requestHasher;
        _dateTimeProvider = dateTimeProvider;
    }

    public async Task<CheckoutQuoteResult> GetQuoteAsync(
        Guid tenantId,
        CheckoutQuoteInput input,
        CancellationToken cancellationToken)
    {
        if (tenantId == Guid.Empty)
        {
            throw new ValidationException(new Dictionary<string, string[]>
            {
                ["tenantId"] = ["Tenant ID is required."]
            });
        }

        if (input.Items is null || input.Items.Count == 0)
        {
            throw new ValidationException(new Dictionary<string, string[]>
            {
                ["items"] = ["At least one item is required for a checkout quote."]
            });
        }

        if (input.Items.Count > MaxLineItems)
        {
            throw new ValidationException(new Dictionary<string, string[]>
            {
                ["items"] = [$"A checkout quote cannot contain more than {MaxLineItems} line items."]
            });
        }

        return await _orderingGateway.CalculateQuoteAsync(
            tenantId,
            input.Items,
            input.Fulfillment,
            input.CouponCode,
            cancellationToken);
    }

    public async Task<CheckoutSubmitOutput> SubmitOrderAsync(
        Guid tenantId,
        CheckoutSubmitInput input,
        string rawIdempotencyKey,
        CancellationToken cancellationToken)
    {
        if (tenantId == Guid.Empty)
        {
            throw new ValidationException(new Dictionary<string, string[]>
            {
                ["tenantId"] = ["Tenant ID is required."]
            });
        }

        var keyHash = _requestHasher.ComputeKeyHash(rawIdempotencyKey);

        if (input.GuestParty is null)
        {
            throw new ValidationException(new Dictionary<string, string[]>
            {
                ["guestParty"] = ["Guest contact information is required for public checkout."]
            });
        }

        if (string.IsNullOrWhiteSpace(input.GuestParty.DisplayName))
        {
            throw new ValidationException(new Dictionary<string, string[]>
            {
                ["guestParty.displayName"] = ["Guest display name is required."]
            });
        }

        if (string.IsNullOrWhiteSpace(input.GuestParty.Phone) && string.IsNullOrWhiteSpace(input.GuestParty.Email))
        {
            throw new ValidationException(new Dictionary<string, string[]>
            {
                ["guestParty"] = ["At least one contact method (phone or email) is required for guest checkout."]
            });
        }

        if (input.Items is null || input.Items.Count == 0)
        {
            throw new ValidationException(new Dictionary<string, string[]>
            {
                ["items"] = ["At least one item is required for order submission."]
            });
        }

        if (input.Items.Count > MaxLineItems)
        {
            throw new ValidationException(new Dictionary<string, string[]>
            {
                ["items"] = [$"An order cannot contain more than {MaxLineItems} line items."]
            });
        }

        if (input.Fulfillment is null || string.IsNullOrWhiteSpace(input.Fulfillment.Method))
        {
            throw new ValidationException(new Dictionary<string, string[]>
            {
                ["fulfillment"] = ["A valid fulfillment method (Pickup or Delivery) is required for checkout."]
            });
        }

        var requestHash = _requestHasher.ComputeRequestHash(
            input.GuestParty,
            input.Items,
            input.Fulfillment,
            input.CouponCode);

        // Fast-path read outside transaction for existing completed records
        var existingRecord = await _idempotencyRepository.GetByKeyHashAsync(tenantId, keyHash, cancellationToken);
        if (existingRecord is not null && existingRecord.IsCompleted)
        {
            if (existingRecord.RequestHash == requestHash)
            {
                var replay = await _orderingGateway.GetOrderReplayAsync(tenantId, existingRecord.OrderId!.Value, cancellationToken);
                if (replay is not null)
                {
                    return new CheckoutSubmitOutput(replay, IsReplay: true);
                }
            }

            throw new ConflictException("The provided Idempotency-Key has already been used with a different request payload.");
        }

        const int maxRetries = 5;
        for (var attempt = 1; attempt <= maxRetries; attempt++)
        {
            try
            {
                return await ExecuteSubmissionAsync(tenantId, input, keyHash, requestHash, cancellationToken);
            }
            catch (IdempotencyKeyClaimConflictException) when (attempt < maxRetries)
            {
                await Task.Delay(50 * attempt, cancellationToken);
                var record = await _idempotencyRepository.GetByKeyHashAsync(tenantId, keyHash, cancellationToken);
                if (record is not null && record.IsCompleted)
                {
                    if (record.RequestHash == requestHash)
                    {
                        var replay = await _orderingGateway.GetOrderReplayAsync(tenantId, record.OrderId!.Value, cancellationToken);
                        if (replay is not null)
                        {
                            return new CheckoutSubmitOutput(replay, IsReplay: true);
                        }
                    }

                    throw new ConflictException("The provided Idempotency-Key has already been used with a different request payload.");
                }
            }
        }

        return await ExecuteSubmissionAsync(tenantId, input, keyHash, requestHash, cancellationToken);
    }

    private async Task<CheckoutSubmitOutput> ExecuteSubmissionAsync(
        Guid tenantId,
        CheckoutSubmitInput input,
        string keyHash,
        string requestHash,
        CancellationToken cancellationToken)
    {
        return await _idempotencyRepository.ExecuteInTransactionAsync(async () =>
        {
            var record = await _idempotencyRepository.GetByKeyHashAsync(tenantId, keyHash, cancellationToken);
            if (record is not null && record.IsCompleted)
            {
                if (record.RequestHash == requestHash)
                {
                    var replay = await _orderingGateway.GetOrderReplayAsync(tenantId, record.OrderId!.Value, cancellationToken);
                    if (replay is not null)
                    {
                        return new CheckoutSubmitOutput(replay, IsReplay: true);
                    }
                }

                throw new ConflictException("The provided Idempotency-Key has already been used with a different request payload.");
            }

            if (record is null)
            {
                record = CheckoutIdempotencyRecord.CreateClaim(
                    Guid.NewGuid(),
                    tenantId,
                    keyHash,
                    requestHash,
                    _dateTimeProvider.UtcNow);

                await _idempotencyRepository.AddAsync(record, cancellationToken);
                await _idempotencyRepository.SaveChangesAsync(cancellationToken);
            }
            else if (record.RequestHash != requestHash)
            {
                throw new ConflictException("The provided Idempotency-Key has already been used with a different request payload.");
            }

            var orderPlacement = await _orderingGateway.PlaceOrderAsync(
                tenantId,
                input.GuestParty,
                input.Items,
                input.Fulfillment,
                input.CouponCode,
                cancellationToken);

            record.Complete(orderPlacement.OrderId, _dateTimeProvider.UtcNow);
            await _idempotencyRepository.SaveChangesAsync(cancellationToken);

            return new CheckoutSubmitOutput(orderPlacement, IsReplay: false);
        }, cancellationToken);
    }
}
