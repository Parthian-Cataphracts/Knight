namespace Ordering.Domain;

/// <summary>
/// Explicit, linear lifecycle of an order within the platform. Delivery and payment
/// lifecycles are deferred to future modules and intentionally omitted here.
/// </summary>
public enum OrderStatus
{
    Pending = 0,
    Confirmed = 1,
    Preparing = 2,
    Ready = 3,
    Completed = 4,
    Cancelled = 5
}
