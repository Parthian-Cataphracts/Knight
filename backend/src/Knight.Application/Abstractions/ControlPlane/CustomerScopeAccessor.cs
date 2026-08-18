namespace Knight.Application.Abstractions.ControlPlane;

/// <summary>
/// Request-scoped implementation of the customer boundary. Registered once per
/// request; only the pipeline that resolves the principal — and tests standing in
/// for it — may call the mutating members.
///
/// The initial state is neither platform nor customer, which is what makes the
/// persistence filter fail closed: a request that never resolved a principal
/// reads nothing at all.
/// </summary>
public sealed class CustomerScopeAccessor : ICustomerScope, ICustomerScopeAccessor
{
    public bool IsPlatformScope { get; private set; }

    public Guid? CustomerId { get; private set; }

    public bool HasCustomer => CustomerId.HasValue;

    public void SetPlatformScope()
    {
        CustomerId = null;
        IsPlatformScope = true;
    }

    public void SetCustomer(Guid customerId)
    {
        CustomerId = customerId;
        IsPlatformScope = false;
    }

    public void Clear()
    {
        CustomerId = null;
        IsPlatformScope = false;
    }
}
