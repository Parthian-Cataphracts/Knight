using Knight.Api.Ingest;
using Knight.Application.Abstractions.ControlPlane;

namespace Knight.Api.ControlPlane;

/// <summary>
/// Establishes the customer boundary for the request from the validated access
/// token, before authorization runs and long before any handler does.
///
/// A control-plane user with a customer_id claim is confined to that customer;
/// one without it is platform staff. A store token is confined to the customer
/// that owns the store — the strictest scope of all, and the reason a store
/// cannot read or write another customer's rows even if a handler forgot to
/// check. Anything else — an unauthenticated caller, an agent token — leaves the
/// scope unset, and the persistence filter then returns nothing rather than
/// everything (docs/authorization.md section 3).
/// </summary>
public sealed class ControlPlaneScopeMiddleware
{
    private readonly RequestDelegate _next;

    public ControlPlaneScopeMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(
        HttpContext context,
        IControlPlanePrincipal principal,
        IStorePrincipal store,
        ICustomerScopeAccessor scope)
    {
        if (principal.IsControlPlaneUser)
        {
            if (principal.CustomerId is { } customerId)
            {
                scope.SetCustomer(customerId);
            }
            else
            {
                scope.SetPlatformScope();
            }
        }
        else if (store.IsStore && store.CustomerId is { } storeCustomerId)
        {
            scope.SetCustomer(storeCustomerId);
        }
        else
        {
            scope.Clear();
        }

        await _next(context);
    }
}
