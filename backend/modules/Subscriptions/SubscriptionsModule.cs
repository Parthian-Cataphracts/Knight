using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Subscriptions;

/// <summary>
/// Bound from configuration (section "Subscriptions").
/// </summary>
public sealed class SubscriptionOptions
{
    public const string SectionName = "Subscriptions";

    /// <summary>
    /// How long a billing period runs. Configurable because the platform bills
    /// monthly today and may bill annually later; nothing in the code assumes a
    /// calendar month.
    /// </summary>
    [Range(typeof(TimeSpan), "1.00:00:00", "366.00:00:00")]
    public TimeSpan BillingPeriod { get; init; } = TimeSpan.FromDays(30);
}

public static class SubscriptionsModule
{
    public static IServiceCollection AddSubscriptionsModule(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<SubscriptionOptions>()
            .Bind(configuration.GetSection(SubscriptionOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddScoped<IEntitlementService, EntitlementService>();
        services.AddScoped<ISubscriptionService, SubscriptionService>();

        return services;
    }
}
