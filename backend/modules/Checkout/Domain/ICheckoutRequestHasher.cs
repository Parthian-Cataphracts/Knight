namespace Checkout.Domain;

public interface ICheckoutRequestHasher
{
    string ComputeKeyHash(string rawIdempotencyKey);

    string ComputeRequestHash(
        CheckoutGuestPartySelection guestParty,
        IReadOnlyList<CheckoutItemSelection> items,
        CheckoutFulfillmentSelection fulfillment,
        string? couponCode = null);
}
