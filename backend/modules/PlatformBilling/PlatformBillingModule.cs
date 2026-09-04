using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using PlatformBilling.Payments;

namespace PlatformBilling;

/// <summary>
/// KNIGHT's own billing — merchant → KNIGHT — kept a separate domain from a
/// store's payment gateway on purpose (docs/self-service-saas-plan.md §3,
/// [`adr/0035`](../../docs/adr/0035-pivot-to-self-service-saas-registration.md)).
///
/// Phase A laid down the domain and its persistence. Phase C wires the services
/// that use it: the public catalogue, the checkout, the payment-provider
/// abstraction (with the simulated provider standing in for an unchosen gateway)
/// and the webhook that is the only thing that activates a paid subscription.
/// </summary>
public static class PlatformBillingModule
{
    public static IServiceCollection AddPlatformBillingModule(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<PlatformBillingOptions>()
            .Bind(configuration.GetSection(PlatformBillingOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        // The provider abstraction. The simulated provider is always registered so
        // a deployment with no real gateway configured still runs the whole
        // journey; a real provider is added alongside it when the product owner
        // chooses one, and selected by name at checkout.
        services.AddSingleton<IPlatformPaymentProvider, SimulatedPaymentProvider>();
        services.AddSingleton<IPlatformPaymentProviderRegistry, PlatformPaymentProviderRegistry>();

        services.AddScoped<IPublicPlanCatalog, PublicPlanCatalog>();
        services.AddScoped<ICheckoutService, CheckoutService>();
        services.AddScoped<IPlatformWebhookService, PlatformWebhookService>();

        return services;
    }
}
