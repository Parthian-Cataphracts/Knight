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

    /// <summary>The length of a billing period, used to roll a subscription forward.</summary>
    [Range(typeof(TimeSpan), "1.00:00:00", "366.00:00:00")]
    public TimeSpan BillingPeriod { get; init; } = TimeSpan.FromDays(30);

    /// <summary>How often the billing run looks for periods that have closed.</summary>
    [Range(typeof(TimeSpan), "00:01:00", "24:00:00")]
    public TimeSpan RunInterval { get; init; } = TimeSpan.FromHours(1);

    /// <summary>
    /// Whether the run issues the invoices it prepares, or leaves them as drafts
    /// for a person to check.
    ///
    /// Drafts by default. Issuing is what consumes a gapless invoice number and
    /// is the point after which an invoice cannot simply be corrected, and this
    /// project has never sent an invoice to anybody — turning that on is a
    /// decision the business makes once, not a default it inherits.
    /// </summary>
    public bool IssueAutomatically { get; init; }

    /// <summary>How many subscriptions one pass will bill. Keeps a backlog from becoming one enormous transaction.</summary>
    [Range(1, 1000)]
    public int RunBatchSize { get; init; } = 100;
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
