namespace Knight.Domain.Common;

/// <summary>
/// Marks a control-plane entity that may belong to a customer. Implementing this
/// is what makes an entity eligible for the customer-isolation query filter
/// applied by the control-plane persistence layer (docs/authorization.md §3).
///
/// The identifier is nullable because these rows are sometimes platform-owned
/// rather than customer-owned — a platform staff account, a system role, an
/// audit entry for a platform-wide action. Those are visible to platform
/// principals only; the filter never reads "no customer" as "every customer".
///
/// Entities that always belong to a customer implement <see cref="ICustomerOwned"/>
/// instead, so their model keeps saying that the owner is mandatory.
/// </summary>
public interface ICustomerScoped
{
    Guid? CustomerId { get; }
}

/// <summary>
/// Marks a control-plane entity that always belongs to exactly one customer — a
/// store, and everything hanging off one. Isolation applies identically to
/// <see cref="ICustomerScoped"/>; the two exist separately because the property
/// types genuinely differ, and the persistence filter must read the mapped
/// column rather than an interface method it cannot translate to SQL.
/// </summary>
public interface ICustomerOwned
{
    Guid CustomerId { get; }
}
