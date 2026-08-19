using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Ingestion;

/// <summary>
/// Bound from configuration (section "Ingestion").
///
/// The caps exist to keep one store from filling the control plane, and they are
/// configuration rather than constants because the right number depends on how
/// many stores a deployment carries — but there is always a number
/// (docs/security-threat-model.md, resource exhaustion).
/// </summary>
public sealed class IngestionOptions
{
    public const string SectionName = "Ingestion";

    [Range(1, 1000)]
    public int MaxErrorsPerBatch { get; init; } = 100;

    [Range(1, 1000)]
    public int MaxEventsPerBatch { get; init; } = 100;

    [Range(1, 5000)]
    public int MaxLogsPerBatch { get; init; } = 500;

    /// <summary>
    /// How long an idempotency key is remembered. Longer than any client's retry
    /// schedule, shorter than the interval at which a store might legitimately
    /// reuse a key it generates from a bounded space.
    /// </summary>
    [Range(typeof(TimeSpan), "00:01:00", "24:00:00")]
    public TimeSpan IdempotencyWindow { get; init; } = TimeSpan.FromHours(1);

    /// <summary>
    /// Requests per minute one store may make across all ingestion endpoints.
    /// Enforced at the pipeline, per store rather than per IP: several stores
    /// commonly share an address, and one of them misbehaving must not silence
    /// the others.
    /// </summary>
    [Range(1, 100000)]
    public int PerStorePermitLimit { get; init; } = 600;
}

public static class IngestionModule
{
    public static IServiceCollection AddIngestionModule(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<IngestionOptions>()
            .Bind(configuration.GetSection(IngestionOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddScoped<IIngestionService, IngestionService>();

        return services;
    }
}
