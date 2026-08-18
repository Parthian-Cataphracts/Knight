using Ordering.Domain;
using Knight.Application.Abstractions.Time;
using Knight.Application.Exceptions;

namespace Ordering;

public sealed class OrderPlacementService : IOrderPlacementService
{
    private const string EntityType = nameof(Order);

    private readonly IOrderRepository _orderRepository;
    private readonly ITenantOrderCounterRepository _counterRepository;
    private readonly IOrderPricingCalculator _pricingCalculator;
    private readonly ICustomerOrderingReader _customerReader;
    private readonly IOrderPromotionRedeemer _promotionRedeemer;
    private readonly IDateTimeProvider _dateTimeProvider;
    private readonly OrderingAuditRecorder _audit;

    public OrderPlacementService(
        IOrderRepository orderRepository,
        ITenantOrderCounterRepository counterRepository,
        IOrderPricingCalculator pricingCalculator,
        ICustomerOrderingReader customerReader,
        IOrderPromotionRedeemer promotionRedeemer,
        IDateTimeProvider dateTimeProvider,
        OrderingAuditRecorder audit)
    {
        _orderRepository = orderRepository;
        _counterRepository = counterRepository;
        _pricingCalculator = pricingCalculator;
        _customerReader = customerReader;
        _promotionRedeemer = promotionRedeemer;
        _dateTimeProvider = dateTimeProvider;
        _audit = audit;
    }

    public async Task<PlaceOrderResult> PlaceOrderAsync(
        Guid tenantId,
        PlaceOrderInput input,
        OrderActorContext? actor,
        CancellationToken cancellationToken)
    {
        if (input.CustomerId.HasValue && input.GuestParty is not null)
        {
            throw new ValidationException(new Dictionary<string, string[]>
            {
                ["party"] = ["Cannot specify both CustomerId and GuestParty in the same order placement request."]
            });
        }

        var calculation = await _pricingCalculator.CalculatePricingAsync(
            tenantId,
            input.Items.ToList(),
            input.Fulfillment,
            input.CouponCode,
            cancellationToken);

        var orderId = Guid.NewGuid();
        var now = _dateTimeProvider.UtcNow;

        OrderPartySnapshot? partySnapshot = null;
        if (input.CustomerId.HasValue)
        {
            var customer = await _customerReader.GetCustomerSnapshotAsync(tenantId, input.CustomerId.Value, cancellationToken);
            if (customer is null || !customer.IsActive)
            {
                throw new ValidationException(new Dictionary<string, string[]>
                {
                    ["customerId"] = [$"Customer '{input.CustomerId.Value}' was not found or is not active in this tenant."]
                });
            }

            partySnapshot = OrderPartySnapshot.CreateFromCustomer(
                Guid.NewGuid(),
                now,
                tenantId,
                orderId,
                customer.CustomerId,
                customer.DisplayName,
                customer.Phone,
                customer.Email);
        }
        else if (input.GuestParty is not null)
        {
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

            partySnapshot = OrderPartySnapshot.CreateFromGuest(
                Guid.NewGuid(),
                now,
                tenantId,
                orderId,
                input.GuestParty.DisplayName.Trim(),
                string.IsNullOrWhiteSpace(input.GuestParty.Phone) ? null : input.GuestParty.Phone.Trim(),
                string.IsNullOrWhiteSpace(input.GuestParty.Email) ? null : input.GuestParty.Email.Trim());
        }

        var orderItems = new List<OrderItem>(calculation.Items.Count);
        foreach (var calculatedItem in calculation.Items)
        {
            var orderItemId = Guid.NewGuid();
            var itemModifiers = new List<OrderItemModifier>(calculatedItem.Modifiers.Count);

            foreach (var mod in calculatedItem.Modifiers)
            {
                var itemModifier = OrderItemModifier.Create(
                    Guid.NewGuid(),
                    tenantId,
                    orderItemId,
                    mod.GroupId,
                    mod.GroupName,
                    mod.ModifierId,
                    mod.ModifierName,
                    mod.PriceDelta,
                    mod.DisplayOrder);

                itemModifiers.Add(itemModifier);
            }

            var orderItem = OrderItem.Create(
                orderItemId,
                tenantId,
                orderId,
                calculatedItem.ProductId,
                calculatedItem.ProductName,
                calculatedItem.VariantId,
                calculatedItem.VariantName,
                calculatedItem.UnitBasePrice,
                calculatedItem.Quantity,
                calculatedItem.DisplayOrder,
                itemModifiers);

            orderItems.Add(orderItem);
        }

        OrderFulfillmentSnapshot? fulfillmentSnapshot = null;
        if (calculation.ResolvedFulfillment is not null)
        {
            var rf = calculation.ResolvedFulfillment;
            fulfillmentSnapshot = rf.Method == OrderFulfillmentMethod.Pickup
                ? OrderFulfillmentSnapshot.CreatePickup(Guid.NewGuid(), now, tenantId, orderId)
                : OrderFulfillmentSnapshot.CreateDelivery(
                    Guid.NewGuid(),
                    now,
                    tenantId,
                    orderId,
                    rf.DeliveryZoneId,
                    rf.DeliveryZoneName,
                    rf.Fee,
                    rf.AddressLine1,
                    rf.AddressLine2,
                    rf.City,
                    rf.PostalCode,
                    rf.Latitude,
                    rf.Longitude);
        }

        OrderPromotionSnapshot? promotionSnapshot = null;
        if (calculation.AppliedPromotion is not null && calculation.DiscountTotal > 0m)
        {
            promotionSnapshot = OrderPromotionSnapshot.Create(
                Guid.NewGuid(),
                tenantId,
                orderId,
                calculation.AppliedPromotion.SourcePromotionId,
                calculation.AppliedPromotion.SourceCouponId,
                calculation.AppliedPromotion.PromotionName,
                calculation.AppliedPromotion.CouponCode,
                calculation.AppliedPromotion.DiscountType,
                calculation.AppliedPromotion.DiscountValue,
                calculation.DiscountTotal,
                now);
        }

        var orderNumber = await _counterRepository.NextOrderNumberAsync(tenantId, cancellationToken);

        var order = Order.Create(
            orderId,
            now,
            tenantId,
            orderNumber,
            calculation.Currency,
            orderItems,
            actor?.UserId,
            actor?.PrincipalType,
            partySnapshot,
            fulfillmentSnapshot,
            calculation.DiscountTotal,
            promotionSnapshot);

        await _orderRepository.AddAsync(order, cancellationToken);

        if (calculation.AppliedPromotion?.SourceCouponId.HasValue == true)
        {
            await _promotionRedeemer.RedeemCouponUsageAsync(
                tenantId,
                calculation.AppliedPromotion.SourceCouponId.Value,
                orderId,
                cancellationToken);
        }

        await _orderRepository.SaveChangesAsync(cancellationToken);

        await _audit.RecordAsync(
            "OrderPlaced",
            tenantId,
            EntityType,
            order.Id,
            cancellationToken,
            actor?.UserId,
            actor?.PrincipalType,
            new Dictionary<string, string>
            {
                ["orderNumber"] = order.OrderNumber.ToString(),
                ["subtotal"] = order.Subtotal.ToString("F2"),
                ["discountTotal"] = order.DiscountTotal.ToString("F2"),
                ["discountedSubtotal"] = order.DiscountedSubtotal.ToString("F2"),
                ["fulfillmentFee"] = order.FulfillmentFee.ToString("F2"),
                ["total"] = order.Total.ToString("F2"),
                ["currency"] = order.Currency,
                ["fulfillmentMethod"] = order.Fulfillment?.Method.ToString() ?? "None",
                ["promotionName"] = calculation.AppliedPromotion?.PromotionName ?? "None",
                ["couponCode"] = calculation.AppliedPromotion?.CouponCode ?? "None"
            });

        return new PlaceOrderResult(
            order.Id,
            order.OrderNumber,
            order.Status,
            order.Currency,
            order.Subtotal,
            order.DiscountTotal,
            order.DiscountedSubtotal,
            order.FulfillmentFee,
            order.Total,
            order.CreatedAt,
            calculation.AppliedPromotion?.PromotionName,
            calculation.AppliedPromotion?.CouponCode);
    }
}
