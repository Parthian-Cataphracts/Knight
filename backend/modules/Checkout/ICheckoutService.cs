using Checkout.Domain;

namespace Checkout;

public sealed record CheckoutQuoteInput(
    IReadOnlyList<CheckoutItemSelection> Items,
    CheckoutFulfillmentSelection? Fulfillment,
    string? CouponCode = null);

public sealed record CheckoutSubmitInput(
    CheckoutGuestPartySelection GuestParty,
    IReadOnlyList<CheckoutItemSelection> Items,
    CheckoutFulfillmentSelection Fulfillment,
    string? CouponCode = null);

public sealed record CheckoutSubmitOutput(
    CheckoutOrderPlacementResult Order,
    bool IsReplay);

public interface ICheckoutService
{
    Task<CheckoutQuoteResult> GetQuoteAsync(
        Guid tenantId,
        CheckoutQuoteInput input,
        CancellationToken cancellationToken);

    Task<CheckoutSubmitOutput> SubmitOrderAsync(
        Guid tenantId,
        CheckoutSubmitInput input,
        string rawIdempotencyKey,
        CancellationToken cancellationToken);
}
