using Knight.Application.Abstractions.Identity;
using Ordering.Domain;

namespace Ordering;

public sealed record PlaceOrderGuestPartyInput(
    string DisplayName,
    string? Phone = null,
    string? Email = null);

public sealed record PlaceOrderInput(
    IReadOnlyCollection<PlaceOrderItemInput> Items,
    Guid? CustomerId = null,
    PlaceOrderGuestPartyInput? GuestParty = null,
    PlaceOrderFulfillmentInput? Fulfillment = null,
    string? CouponCode = null);

public sealed record PlaceOrderItemInput(
    Guid ProductId,
    Guid? VariantId,
    int Quantity,
    IReadOnlyCollection<Guid>? ModifierIds = null);

public sealed record OrderActorContext(
    Guid? UserId = null,
    PrincipalType? PrincipalType = null);

public sealed record PlaceOrderResult(
    Guid OrderId,
    long OrderNumber,
    OrderStatus Status,
    string Currency,
    decimal Subtotal,
    decimal DiscountTotal,
    decimal DiscountedSubtotal,
    decimal FulfillmentFee,
    decimal Total,
    DateTimeOffset CreatedAt,
    string? PromotionName = null,
    string? CouponCode = null);

public interface IOrderPlacementService
{
    Task<PlaceOrderResult> PlaceOrderAsync(
        Guid tenantId,
        PlaceOrderInput input,
        OrderActorContext? actor = null,
        CancellationToken cancellationToken = default);
}
