using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Servers;

/// <summary>
/// Bound from configuration (section "Servers").
/// </summary>
public sealed class ServerOptions
{
    public const string SectionName = "Servers";

    /// <summary>
    /// How often an agent is told to check in. Advertised in the enrolment and
    /// heartbeat responses so the interval is KNIGHT's decision, not the agent's
    /// — an agent that chose its own could quietly stop reporting by choosing a
    /// long one.
    /// </summary>
    [Range(typeof(TimeSpan), "00:00:15", "01:00:00")]
    public TimeSpan HeartbeatInterval { get; init; } = TimeSpan.FromMinutes(1);

    /// <summary>
    /// How many intervals of silence before a machine is presumed down. Three,
    /// because one missed heartbeat is a network hiccup and paging somebody for
    /// it is how alerts get ignored (docs/observability.md section 8).
    /// </summary>
    [Range(2, 10)]
    public int MissedIntervalsBeforeOffline { get; init; } = 3;

    /// <summary>How often the sweep looks for machines that have gone quiet.</summary>
    [Range(typeof(TimeSpan), "00:00:15", "00:30:00")]
    public TimeSpan EvaluationInterval { get; init; } = TimeSpan.FromMinutes(1);

    /// <summary>
    /// How long metric samples are kept. The highest-volume table in the schema,
    /// so this is not a nicety: it is the difference between a database that
    /// works next year and one that does not (docs/observability.md section 7).
    /// </summary>
    [Range(typeof(TimeSpan), "1.00:00:00", "365.00:00:00")]
    public TimeSpan MetricRetention { get; init; } = TimeSpan.FromDays(30);

    /// <summary>How often the retention sweep runs. Hourly is plenty for a daily-scale window.</summary>
    [Range(typeof(TimeSpan), "00:05:00", "24:00:00")]
    public TimeSpan RetentionInterval { get; init; } = TimeSpan.FromHours(1);

    [Range(1, 100)]
    public double DiskCriticalPercent { get; init; } = 90;

    [Range(1, 100)]
    public double MemoryCriticalPercent { get; init; } = 95;

    [Range(1, 100)]
    public double CpuCriticalPercent { get; init; } = 95;
}

public static class ServersModule
{
    public static IServiceCollection AddServersModule(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<ServerOptions>()
            .Bind(configuration.GetSection(ServerOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddScoped<IServerService, ServerService>();
        services.AddScoped<IAgentService, AgentService>();
        services.AddScoped<IMonitoringService, MonitoringService>();

        return services;
    }
}
