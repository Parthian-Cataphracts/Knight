using Checkout.Domain;
using Microsoft.Extensions.DependencyInjection;

namespace Checkout;

public static class CheckoutModule
{
    public static IServiceCollection AddCheckoutModule(this IServiceCollection services)
    {
        services.AddSingleton<ICheckoutRequestHasher, CheckoutRequestHasher>();
        services.AddScoped<ICheckoutService, CheckoutService>();

        return services;
    }
}
