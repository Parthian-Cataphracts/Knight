using Checkout.Domain;
using Ordering;
using Ordering.Domain;

namespace Knight.Infrastructure.Adapters;

public sealed class CheckoutOrderingGateway : ICheckoutOrderingGateway
{
    private readonly IOrderPricingCalculator _pricingCalculator;
    private readonly IOrderPlacementService _placementService;
    private readonly IOrderRepository _orderRepository;

    public CheckoutOrderingGateway(
        IOrderPricingCalculator pricingCalculator,
        IOrderPlacementService placementService,
        IOrderRepository orderRepository)
    {
        _pricingCalculator = pricingCalculator;
        _placementService = placementService;
        _orderRepository = orderRepository;
    }

    public async Task<CheckoutQuoteResult> CalculateQuoteAsync(
        Guid tenantId,
        IReadOnlyList<CheckoutItemSelection> items,
        CheckoutFulfillmentSelection? fulfillment,
        string? couponCode,
        CancellationToken cancellationToken)
    {
        var mappedItems = items.Select(i => new PlaceOrderItemInput(
            i.ProductId,
            i.VariantId,
            i.Quantity,
            i.ModifierIds)).ToArray();

        PlaceOrderFulfillmentInput? mappedFulfillment = null;
        if (fulfillment is not null && !string.IsNullOrWhiteSpace(fulfillment.Method))
        {
            var isPickup = string.Equals(fulfillment.Method, "Pickup", StringComparison.OrdinalIgnoreCase);
            var method = isPickup ? OrderFulfillmentMethod.Pickup : OrderFulfillmentMethod.Delivery;

            var address = isPickup ? null : new PlaceOrderAddressInput(
                fulfillment.AddressLine1,
                fulfillment.AddressLine2,
                fulfillment.City,
                fulfillment.PostalCode,
                fulfillment.Latitude.HasValue ? (double)fulfillment.Latitude.Value : null,
                fulfillment.Longitude.HasValue ? (double)fulfillment.Longitude.Value : null);

            mappedFulfillment = new PlaceOrderFulfillmentInput(
                method,
                fulfillment.DeliveryZoneId,
                address);
        }

        var result = await _pricingCalculator.CalculatePricingAsync(
            tenantId,
            mappedItems,
            mappedFulfillment,
            couponCode,
            cancellationToken);

        var quoteItems = result.Items.Select(item => new CheckoutQuoteItemResult(
            item.ProductId,
            item.ProductName,
            item.VariantId,
            item.VariantName,
            item.Quantity,
            item.UnitBasePrice,
            item.UnitModifierTotal,
            item.UnitPrice,
            item.LineTotal,
            item.Modifiers.Select(m => new CheckoutQuoteModifierResult(
                m.ModifierId,
                m.ModifierName,
                m.PriceDelta)).ToArray()
        )).ToArray();

        AppliedPromotionQuoteResult? appliedPromotion = null;
        if (result.AppliedPromotion is not null)
        {
            appliedPromotion = new AppliedPromotionQuoteResult(
                result.AppliedPromotion.PromotionName,
                result.AppliedPromotion.DiscountType,
                result.AppliedPromotion.DiscountValue,
                result.AppliedPromotion.DiscountAmount,
                result.AppliedPromotion.CouponCode);
        }

        return new CheckoutQuoteResult(
            result.Currency,
            result.Subtotal,
            result.DiscountTotal,
            result.DiscountedSubtotal,
            result.FulfillmentFee,
            result.Total,
            quoteItems,
            appliedPromotion);
    }

    public async Task<CheckoutOrderPlacementResult> PlaceOrderAsync(
        Guid tenantId,
        CheckoutGuestPartySelection guestParty,
        IReadOnlyList<CheckoutItemSelection> items,
        CheckoutFulfillmentSelection fulfillment,
        string? couponCode,
        CancellationToken cancellationToken)
    {
        var mappedParty = new PlaceOrderGuestPartyInput(
            guestParty.DisplayName,
            guestParty.Phone,
            guestParty.Email);

        var mappedItems = items.Select(i => new PlaceOrderItemInput(
            i.ProductId,
            i.VariantId,
            i.Quantity,
            i.ModifierIds)).ToArray();

        var isPickup = string.Equals(fulfillment.Method, "Pickup", StringComparison.OrdinalIgnoreCase);
        var method = isPickup ? OrderFulfillmentMethod.Pickup : OrderFulfillmentMethod.Delivery;

        var address = isPickup ? null : new PlaceOrderAddressInput(
            fulfillment.AddressLine1,
            fulfillment.AddressLine2,
            fulfillment.City,
            fulfillment.PostalCode,
            fulfillment.Latitude.HasValue ? (double)fulfillment.Latitude.Value : null,
            fulfillment.Longitude.HasValue ? (double)fulfillment.Longitude.Value : null);

        var mappedFulfillment = new PlaceOrderFulfillmentInput(
            method,
            fulfillment.DeliveryZoneId,
            address);

        var input = new PlaceOrderInput(
            mappedItems,
            CustomerId: null,
            mappedParty,
            mappedFulfillment,
            couponCode);

        var result = await _placementService.PlaceOrderAsync(
            tenantId,
            input,
            actor: null,
            cancellationToken);

        return new CheckoutOrderPlacementResult(
            result.OrderId,
            result.OrderNumber,
            result.Status.ToString(),
            result.Currency,
            result.Subtotal,
            result.DiscountTotal,
            result.DiscountedSubtotal,
            result.FulfillmentFee,
            result.Total,
            fulfillment.Method,
            result.CreatedAt,
            result.PromotionName,
            result.CouponCode);
    }

    public async Task<CheckoutOrderPlacementResult?> GetOrderReplayAsync(
        Guid tenantId,
        Guid orderId,
        CancellationToken cancellationToken)
    {
        var order = await _orderRepository.GetByIdAsync(tenantId, orderId, cancellationToken);
        if (order is null)
        {
            return null;
        }

        return new CheckoutOrderPlacementResult(
            order.Id,
            order.OrderNumber,
            order.Status.ToString(),
            order.Currency,
            order.Subtotal,
            order.DiscountTotal,
            order.DiscountedSubtotal,
            order.FulfillmentFee,
            order.Total,
            order.Fulfillment?.Method.ToString() ?? "None",
            order.CreatedAt,
            order.Promotion?.PromotionName,
            order.Promotion?.CouponCode);
    }
}
