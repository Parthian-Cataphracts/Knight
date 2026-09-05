using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Billing;

/// <summary>
/// Bound from configuration (section "Billing").
/// </summary>
public sealed class BillingOptions : IValidatableObject
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

    /// <summary>
    /// Tax rate per currency, as a fraction of the subtotal (0.09 = 9%). KNIGHT
    /// does not compute tax from a jurisdiction — that is a legal question, not a
    /// rounding one — so the rate for each currency it bills in is set here by
    /// hand and applied to a draft when it is prepared. A currency with no entry,
    /// or a rate of zero, is billed tax-free, which is the previous behaviour.
    /// Keyed by ISO 4217 code; lookup is case-insensitive.
    /// </summary>
    public IDictionary<string, decimal> TaxRates { get; init; } =
        new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        foreach (var (currency, rate) in TaxRates)
        {
            if (rate is < 0m or > 1m)
            {
                yield return new ValidationResult(
                    $"The tax rate for '{currency}' must be a fraction between 0 and 1 (0.09 = 9%), not {rate}.",
                    [nameof(TaxRates)]);
            }
        }
    }
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
