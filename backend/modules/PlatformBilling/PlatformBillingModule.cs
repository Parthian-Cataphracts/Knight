using Microsoft.Extensions.DependencyInjection;

namespace PlatformBilling;

/// <summary>
/// KNIGHT's own billing — merchant → KNIGHT — kept a separate domain from a
/// store's payment gateway on purpose (docs/self-service-saas-plan.md §3,
/// [`adr/0035`](../../docs/adr/0035-pivot-to-self-service-saas-registration.md)).
///
/// Phase A registers no application services: it lays down the domain and its
/// persistence. The checkout and webhook services arrive in phase C, and this is
/// where they will be wired.
/// </summary>
public static class PlatformBillingModule
{
    public static IServiceCollection AddPlatformBillingModule(this IServiceCollection services)
    {
        return services;
    }
}
