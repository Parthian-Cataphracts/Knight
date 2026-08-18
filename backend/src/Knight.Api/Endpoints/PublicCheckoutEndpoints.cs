using Catalog;
using Checkout;
using Checkout.Domain;
using Ordering;
using Knight.Api.Authorization;
using Knight.Application.Abstractions.Tenancy;
using Knight.Application.Exceptions;
using Knight.Contracts.Checkout;

namespace Knight.Api.Endpoints;

public static class PublicCheckoutEndpoints
{
    public static IEndpointRouteBuilder MapPublicCheckoutEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/public/checkout")
            .RequireFeature(OrderingFeature.Key)
            .RequireFeature(CatalogFeature.Key);

        group.MapPost("/quote", async (
            CheckoutQuoteRequest request,
            ITenantContext tenantContext,
            ICheckoutService checkoutService,
            CancellationToken cancellationToken) =>
        {
            if (!tenantContext.HasTenant)
            {
                return Results.NotFound(new { message = "Tenant not found." });
            }

            var items = request.Items?.Select(i => new CheckoutItemSelection(
                i.ProductId,
                i.VariantId,
                i.Quantity,
                i.ModifierIds)).ToArray() ?? [];

            CheckoutFulfillmentSelection? fulfillment = null;
            if (request.Fulfillment is not null)
            {
                fulfillment = new CheckoutFulfillmentSelection(
                    request.Fulfillment.Method,
                    request.Fulfillment.DeliveryZoneId,
                    request.Fulfillment.AddressLine1,
                    request.Fulfillment.AddressLine2,
                    request.Fulfillment.City,
                    request.Fulfillment.PostalCode,
                    request.Fulfillment.Latitude,
                    request.Fulfillment.Longitude);
            }

            var quote = await checkoutService.GetQuoteAsync(
                tenantContext.TenantId!.Value,
                new CheckoutQuoteInput(items, fulfillment, request.CouponCode),
                cancellationToken);

            var quoteItems = quote.Items.Select(item => new CheckoutQuoteItemResponse(
                item.ProductId,
                item.ProductName,
                item.VariantId,
                item.VariantName,
                item.Quantity,
                item.UnitBasePrice,
                item.UnitModifierTotal,
                item.UnitPrice,
                item.LineTotal,
                item.Modifiers.Select(m => new CheckoutQuoteItemModifierResponse(
                    m.ModifierId,
                    m.ModifierName,
                    m.Price)).ToArray()
            )).ToArray();

            AppliedPromotionQuoteResponse? appliedPromotion = null;
            if (quote.AppliedPromotion is not null)
            {
                appliedPromotion = new AppliedPromotionQuoteResponse(
                    quote.AppliedPromotion.Name,
                    quote.AppliedPromotion.DiscountType,
                    quote.AppliedPromotion.DiscountValue,
                    quote.AppliedPromotion.DiscountAmount,
                    quote.AppliedPromotion.CouponCode);
            }

            var response = new CheckoutQuoteResponse(
                quote.Currency,
                quote.Subtotal,
                quote.DiscountTotal,
                quote.DiscountedSubtotal,
                quote.FulfillmentFee,
                quote.Total,
                quoteItems,
                appliedPromotion);

            return Results.Ok(response);
        })
        .RequireRateLimiting("checkout_quote");

        group.MapPost("/orders", async (
            HttpContext httpContext,
            CheckoutSubmitRequest request,
            ITenantContext tenantContext,
            ICheckoutService checkoutService,
            CancellationToken cancellationToken) =>
        {
            if (!tenantContext.HasTenant)
            {
                return Results.NotFound(new { message = "Tenant not found." });
            }

            if (!httpContext.Request.Headers.TryGetValue("Idempotency-Key", out var rawKeyValues) ||
                string.IsNullOrWhiteSpace(rawKeyValues.ToString()))
            {
                throw new ValidationException(new Dictionary<string, string[]>
                {
                    ["Idempotency-Key"] = ["The Idempotency-Key header is required."]
                });
            }

            var rawIdempotencyKey = rawKeyValues.ToString();

            if (request.GuestParty is null)
            {
                throw new ValidationException(new Dictionary<string, string[]>
                {
                    ["guestParty"] = ["Guest contact information is required for public checkout."]
                });
            }

            if (request.Fulfillment is null)
            {
                throw new ValidationException(new Dictionary<string, string[]>
                {
                    ["fulfillment"] = ["A valid fulfillment method is required for public checkout."]
                });
            }

            var guestParty = new CheckoutGuestPartySelection(
                request.GuestParty.DisplayName,
                request.GuestParty.Phone,
                request.GuestParty.Email);

            var items = request.Items?.Select(i => new CheckoutItemSelection(
                i.ProductId,
                i.VariantId,
                i.Quantity,
                i.ModifierIds)).ToArray() ?? [];

            var fulfillment = new CheckoutFulfillmentSelection(
                request.Fulfillment.Method,
                request.Fulfillment.DeliveryZoneId,
                request.Fulfillment.AddressLine1,
                request.Fulfillment.AddressLine2,
                request.Fulfillment.City,
                request.Fulfillment.PostalCode,
                request.Fulfillment.Latitude,
                request.Fulfillment.Longitude);

            var output = await checkoutService.SubmitOrderAsync(
                tenantContext.TenantId!.Value,
                new CheckoutSubmitInput(guestParty, items, fulfillment, request.CouponCode),
                rawIdempotencyKey,
                cancellationToken);

            var response = new CheckoutSubmitResponse(
                output.Order.OrderId,
                output.Order.OrderNumber,
                output.Order.Status,
                output.Order.Currency,
                output.Order.Subtotal,
                output.Order.DiscountTotal,
                output.Order.DiscountedSubtotal,
                output.Order.FulfillmentFee,
                output.Order.Total,
                output.Order.FulfillmentMethod,
                output.Order.CreatedAt,
                output.Order.PromotionName,
                output.Order.CouponCode);

            if (output.IsReplay)
            {
                return Results.Ok(response);
            }

            return Results.Created($"/api/public/checkout/orders/{output.Order.OrderId}", response);
        })
        .RequireRateLimiting("checkout_submit");

        return app;
    }
}
