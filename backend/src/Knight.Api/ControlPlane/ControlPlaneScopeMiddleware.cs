using Knight.Application.Abstractions.ControlPlane;

namespace Knight.Api.ControlPlane;

/// <summary>
/// Establishes the customer boundary for the request from the validated access
/// token, before authorization runs and long before any handler does.
///
/// A control-plane user with a customer_id claim is confined to that customer;
/// one without it is platform staff. Anything else — an unauthenticated caller,
/// a store token, an agent token — leaves the scope unset, and the persistence
/// filter then returns nothing rather than everything
/// (docs/authorization.md section 3).
/// </summary>
public sealed class ControlPlaneScopeMiddleware
{
    private readonly RequestDelegate _next;

    public ControlPlaneScopeMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context, IControlPlanePrincipal principal, ICustomerScopeAccessor scope)
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
        else
        {
            scope.Clear();
        }

        await _next(context);
    }
}
