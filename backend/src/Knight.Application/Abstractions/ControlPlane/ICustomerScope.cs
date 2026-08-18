namespace Knight.Application.Abstractions.ControlPlane;

/// <summary>
/// The customer boundary the current request runs inside. Read by the
/// control-plane persistence layer to filter every customer-owned entity, so a
/// forgotten <c>where customerId = ...</c> in a handler cannot become a data leak
/// (docs/authorization.md §3).
///
/// Both properties are deliberately restrictive: an unresolved scope is neither
/// platform-wide nor customer-wide, and yields no rows at all.
/// </summary>
public interface ICustomerScope
{
    /// <summary>True for platform staff, who legitimately operate across customers.</summary>
    bool IsPlatformScope { get; }

    /// <summary>Set for a customer-scoped principal; null for platform staff and unauthenticated callers.</summary>
    Guid? CustomerId { get; }

    bool HasCustomer { get; }
}

/// <summary>
/// Write side of <see cref="ICustomerScope"/>. Only the request pipeline and
/// tests may set the scope; application and domain code only ever reads it.
/// </summary>
public interface ICustomerScopeAccessor
{
    void SetPlatformScope();

    void SetCustomer(Guid customerId);

    void Clear();
}
