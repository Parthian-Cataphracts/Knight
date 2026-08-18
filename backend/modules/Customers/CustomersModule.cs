using Microsoft.Extensions.DependencyInjection;

namespace Customers;

public static class CustomersModule
{
    public static IServiceCollection AddCustomersModule(this IServiceCollection services)
    {
        services.AddScoped<ICustomerManagementService, CustomerManagementService>();

        return services;
    }
}
