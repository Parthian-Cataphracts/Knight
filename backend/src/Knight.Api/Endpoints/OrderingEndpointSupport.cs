using Ordering.Domain;
using Knight.Application.Exceptions;
using Knight.Contracts.Ordering;

namespace Knight.Api.Endpoints;

internal static class OrderingEndpointSupport
{
    internal static OrderDetailResponse ToDetailResponse(Order order)
    {
        return new OrderDetailResponse
        {
            Id = order.Id,
            OrderNumber = order.OrderNumber,
            Status = order.Status.ToString(),
            Currency = order.Currency,
            Subtotal = order.Subtotal,
            DiscountTotal = order.DiscountTotal,
            DiscountedSubtotal = order.DiscountedSubtotal,
            FulfillmentFee = order.FulfillmentFee,
            Total = order.Total,
            CreatedAt = order.CreatedAt,
            UpdatedAt = order.UpdatedAt,
            CompletedAt = order.CompletedAt,
            CancelledAt = order.CancelledAt,
            CancellationReason = order.CancellationReason,
            Party = order.Party is null ? null : ToPartyResponse(order.Party),
            Fulfillment = order.Fulfillment is null ? null : ToFulfillmentResponse(order.Fulfillment),
            Promotion = order.Promotion is null ? null : ToPromotionResponse(order.Promotion),
            Items = order.Items.OrderBy(i => i.DisplayOrder).Select(ToItemResponse).ToArray(),
            StatusHistory = order.StatusHistory.OrderBy(h => h.ChangedAt).Select(ToHistoryResponse).ToArray()
        };
    }

    internal static OrderSummaryResponse ToSummaryResponse(Order order)
    {
        return new OrderSummaryResponse
        {
            Id = order.Id,
            OrderNumber = order.OrderNumber,
            Status = order.Status.ToString(),
            Currency = order.Currency,
            Subtotal = order.Subtotal,
            DiscountTotal = order.DiscountTotal,
            DiscountedSubtotal = order.DiscountedSubtotal,
            Total = order.Total,
            ItemCount = order.Items.Count,
            CustomerDisplayName = order.Party?.DisplayName,
            FulfillmentMethod = order.Fulfillment?.Method.ToString(),
            PromotionName = order.Promotion?.PromotionName,
            CouponCode = order.Promotion?.CouponCode,
            CreatedAt = order.CreatedAt,
            CompletedAt = order.CompletedAt,
            CancelledAt = order.CancelledAt
        };
    }

    internal static OrderPromotionResponse ToPromotionResponse(OrderPromotionSnapshot snapshot)
    {
        return new OrderPromotionResponse
        {
            SourcePromotionId = snapshot.SourcePromotionId,
            SourceCouponId = snapshot.SourceCouponId,
            PromotionName = snapshot.PromotionName,
            CouponCode = snapshot.CouponCode,
            DiscountType = snapshot.DiscountType,
            DiscountValue = snapshot.DiscountValue,
            DiscountAmount = snapshot.DiscountAmount
        };
    }

    internal static OrderPartyResponse ToPartyResponse(OrderPartySnapshot party)
    {
        return new OrderPartyResponse
        {
            SourceCustomerId = party.SourceCustomerId,
            DisplayName = party.DisplayName,
            Phone = party.Phone,
            Email = party.Email
        };
    }

    internal static OrderFulfillmentResponse ToFulfillmentResponse(OrderFulfillmentSnapshot fulfillment)
    {
        return new OrderFulfillmentResponse
        {
            Method = fulfillment.Method.ToString(),
            Fee = fulfillment.FulfillmentFee,
            Delivery = fulfillment.Method == OrderFulfillmentMethod.Delivery
                ? new OrderDeliveryResponse
                {
                    ZoneName = fulfillment.DeliveryZoneName,
                    AddressLine1 = fulfillment.AddressLine1,
                    AddressLine2 = fulfillment.AddressLine2,
                    City = fulfillment.City,
                    PostalCode = fulfillment.PostalCode,
                    Latitude = fulfillment.Latitude,
                    Longitude = fulfillment.Longitude
                }
                : null
        };
    }

    internal static OrderItemResponse ToItemResponse(OrderItem item)
    {
        return new OrderItemResponse
        {
            Id = item.Id,
            SourceProductId = item.SourceProductId,
            ProductName = item.ProductName,
            SourceVariantId = item.SourceVariantId,
            VariantName = item.VariantName,
            UnitBasePrice = item.UnitBasePrice,
            Quantity = item.Quantity,
            UnitModifierTotal = item.UnitModifierTotal,
            UnitPrice = item.UnitPrice,
            LineTotal = item.LineTotal,
            DisplayOrder = item.DisplayOrder,
            // Surfaced in the server-authoritative order captured at placement, the
            // same way items are — otherwise the canonical DisplayOrder would be
            // unobservable and the response order would follow EF materialization.
            Modifiers = item.Modifiers.OrderBy(m => m.DisplayOrder).Select(ToModifierResponse).ToArray()
        };
    }

    internal static OrderItemModifierResponse ToModifierResponse(OrderItemModifier modifier)
    {
        return new OrderItemModifierResponse
        {
            Id = modifier.Id,
            SourceModifierGroupId = modifier.SourceModifierGroupId,
            ModifierGroupName = modifier.ModifierGroupName,
            SourceModifierId = modifier.SourceModifierId,
            ModifierName = modifier.ModifierName,
            UnitPriceDelta = modifier.UnitPriceDelta,
            DisplayOrder = modifier.DisplayOrder
        };
    }

    internal static OrderStatusHistoryResponse ToHistoryResponse(OrderStatusHistory history)
    {
        return new OrderStatusHistoryResponse
        {
            Id = history.Id,
            FromStatus = history.FromStatus?.ToString(),
            ToStatus = history.ToStatus.ToString(),
            ChangedAt = history.ChangedAt,
            ChangedByUserId = history.ChangedByUserId,
            ChangedByPrincipalType = history.ChangedByPrincipalType?.ToString(),
            Reason = history.Reason
        };
    }

    internal static OrderStatus ParseStatus(string rawStatus)
    {
        if (Enum.TryParse<OrderStatus>(rawStatus, ignoreCase: true, out var parsed))
        {
            return parsed;
        }

        throw new ValidationException(new Dictionary<string, string[]>
        {
            ["status"] = [$"Unknown order status '{rawStatus}'."]
        });
    }
}
