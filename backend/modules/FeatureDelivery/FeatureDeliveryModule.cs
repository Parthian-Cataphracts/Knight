using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace FeatureDelivery;

/// <summary>
/// Bound from configuration (section "FeatureDelivery").
/// </summary>
public sealed class FeatureDeliveryOptions
{
    public const string SectionName = "FeatureDelivery";

    /// <summary>
    /// How long an agent may hold a claimed job before it is presumed dead.
    ///
    /// Generous rather than tight: the longest step is a migration, and a
    /// migration that takes eleven minutes on a large table has not failed. The
    /// cost of being wrong in the other direction is a job requeued while it is
    /// still running, which the step-level idempotency survives but nobody
    /// enjoys reading in a log.
    /// </summary>
    [Range(typeof(TimeSpan), "00:01:00", "02:00:00")]
    public TimeSpan JobClaimTimeout { get; init; } = TimeSpan.FromMinutes(15);

    /// <summary>How long a minted artifact download URL stays valid. One fetch, not one day.</summary>
    [Range(typeof(TimeSpan), "00:00:30", "01:00:00")]
    public TimeSpan ArtifactUrlLifetime { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>How often the sweep looks for jobs whose claim has lapsed.</summary>
    [Range(typeof(TimeSpan), "00:00:15", "00:30:00")]
    public TimeSpan JobSweepInterval { get; init; } = TimeSpan.FromMinutes(1);

    /// <summary>How many attempts a job gets before it fails for good.</summary>
    [Range(1, 10)]
    public int MaxJobAttempts { get; init; } = 3;
}

public static class FeatureDeliveryModule
{
    public static IServiceCollection AddFeatureDeliveryModule(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<FeatureDeliveryOptions>()
            .Bind(configuration.GetSection(FeatureDeliveryOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddScoped<IFeatureDeliveryService, FeatureDeliveryService>();
        services.AddScoped<IFeatureRolloutService, FeatureRolloutService>();
        services.AddScoped<IAgentJobService, AgentJobService>();

        return services;
    }
}
