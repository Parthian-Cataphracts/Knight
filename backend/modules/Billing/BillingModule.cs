using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Billing;

/// <summary>
/// Bound from configuration (section "Billing").
/// </summary>
public sealed class BillingOptions
{
    public const string SectionName = "Billing";

    /// <summary>How long after issue an invoice falls due.</summary>
    [Range(typeof(TimeSpan), "1.00:00:00", "180.00:00:00")]
    public TimeSpan PaymentTerms { get; init; } = TimeSpan.FromDays(14);
}

public static class BillingModule
{
    public static IServiceCollection AddBillingModule(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<BillingOptions>()
            .Bind(configuration.GetSection(BillingOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddScoped<IBillingService, BillingService>();

        return services;
    }
}
