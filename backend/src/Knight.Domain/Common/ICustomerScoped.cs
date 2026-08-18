namespace Knight.Domain.Common;

/// <summary>
/// Marks a control-plane entity as owned by a single customer. Implementing this
/// is what makes an entity eligible for the customer-isolation query filter
/// applied by the control-plane persistence layer (docs/authorization.md §3).
///
/// The identifier is nullable because some rows are platform-owned rather than
/// customer-owned — a platform staff account, or an audit entry recorded for a
/// platform-wide action. Those rows are visible to platform principals only; the
/// filter never treats "no customer" as "every customer".
/// </summary>
public interface ICustomerScoped
{
    Guid? CustomerId { get; }
}
