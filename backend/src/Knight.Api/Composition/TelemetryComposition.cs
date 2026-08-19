using System.ComponentModel.DataAnnotations;
using System.Diagnostics;
using Knight.Application.Abstractions.Observability;
using Knight.Infrastructure.Telemetry;
using Microsoft.Extensions.Options;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace Knight.Api.Composition;

/// <summary>
/// Bound from configuration (section "Telemetry").
/// </summary>
public sealed class TelemetryOptions
{
    public const string SectionName = "Telemetry";

    /// <summary>
    /// Whether traces and metrics are exported at all. Off by default, including
    /// in Development, because an SDK that cannot reach a collector spends the
    /// process's time retrying and fills the log with its own failures
    /// (docs/observability.md §4).
    /// </summary>
    public bool Enabled { get; init; }

    /// <summary>The OTLP endpoint. Anything a collector, Jaeger or a vendor accepts; nothing custom is invented.</summary>
    public string? OtlpEndpoint { get; init; }

    [Required]
    public string ServiceName { get; init; } = "knight-control-plane";

    /// <summary>
    /// The fraction of traces kept. Full sampling is correct until there is
    /// enough traffic to measure, and guessing a rate before then throws away
    /// exactly the traces that would have shown why something is slow.
    /// </summary>
    [Range(0.0, 1.0)]
    public double SampleRatio { get; init; } = 1.0;

    /// <summary>Whether database command spans are recorded. Verbose, and the first thing wanted when a screen is slow.</summary>
    public bool TraceDatabase { get; init; } = true;
}

/// <summary>
/// KNIGHT's own telemetry: traces, metrics and the retention sweep that keeps
/// the tables they describe from growing without end (docs/observability.md).
///
/// Instrumentation is deliberately standard — ASP.NET Core, HttpClient, EF Core
/// — so a collector can be attached later without touching code. The one custom
/// piece is the meter KNIGHT publishes about itself, because no library can know
/// what "an entitlement that was never installed" means.
/// </summary>
public static class TelemetryComposition
{
    /// <summary>
    /// The activity source for work that is not an HTTP request: background
    /// sweeps, job execution. Without it those run untraced, which is precisely
    /// where the hard-to-diagnose problems live.
    /// </summary>
    public static readonly ActivitySource ActivitySource = new("Knight.ControlPlane");

    public static IServiceCollection AddKnightTelemetry(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddOptions<TelemetryOptions>()
            .Bind(configuration.GetSection(TelemetryOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddOptions<RetentionOptions>()
            .Bind(configuration.GetSection(RetentionOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        var options = configuration.GetSection(TelemetryOptions.SectionName).Get<TelemetryOptions>()
            ?? new TelemetryOptions();

        // The meter is registered whether or not anything exports it. Recording
        // into a meter nobody listens to is nearly free, and making the
        // instruments conditional would mean the code that records measurements
        // has to know whether telemetry is on.
        services.AddMetrics();
        services.AddSingleton<KnightMetrics>();
        services.AddSingleton<IKnightMetrics>(provider => provider.GetRequiredService<KnightMetrics>());

        // The gauge source and the retention service are internal to
        // Infrastructure, where the schema they read lives; registering them is
        // that assembly's job, not this one's.
        services.AddKnightTelemetryInfrastructure();
        services.AddHostedService<RetentionWorker>();
        services.AddHostedService<GaugeRegistration>();

        if (!options.Enabled)
        {
            return services;
        }

        services.AddOpenTelemetry()
            .ConfigureResource(resource => resource.AddService(options.ServiceName))
            .WithTracing(tracing =>
            {
                tracing
                    .SetSampler(new TraceIdRatioBasedSampler(options.SampleRatio))
                    .AddSource(ActivitySource.Name)
                    .AddAspNetCoreInstrumentation(instrumentation =>
                    {
                        // Health probes run every few seconds forever and say
                        // nothing about how the product behaves.
                        instrumentation.Filter = context =>
                            !context.Request.Path.StartsWithSegments("/health");
                    })

                    // Outbound calls to stores. This is also what propagates
                    // traceparent to them, so a store's own trace joins KNIGHT's
                    // rather than starting a disconnected one.
                    .AddHttpClientInstrumentation();

                if (options.TraceDatabase)
                {
                    // Statement text stays off, which is the instrumentation's
                    // default: a query's text can carry customer data, and
                    // knowing which query was slow does not require reading it.
                    tracing.AddEntityFrameworkCoreInstrumentation();
                }

                if (!string.IsNullOrWhiteSpace(options.OtlpEndpoint))
                {
                    tracing.AddOtlpExporter(exporter => exporter.Endpoint = new Uri(options.OtlpEndpoint));
                }
            })
            .WithMetrics(metrics =>
            {
                metrics
                    .AddAspNetCoreInstrumentation()
                    .AddHttpClientInstrumentation()

                    // The instruments KNIGHT publishes about itself. Without
                    // this line every custom measurement is recorded and then
                    // dropped, which is indistinguishable from a healthy system.
                    .AddMeter(KnightMetrics.MeterName);

                if (!string.IsNullOrWhiteSpace(options.OtlpEndpoint))
                {
                    metrics.AddOtlpExporter(exporter => exporter.Endpoint = new Uri(options.OtlpEndpoint));
                }
            });

        return services;
    }
}

/// <summary>
/// Attaches the gauge source once the host is up.
///
/// Registered as a hosted service rather than done during composition because
/// the gauges read the database, and reading it while the container is still
/// being built is how a startup deadlock is written.
/// </summary>
internal sealed class GaugeRegistration : IHostedService
{
    private readonly KnightMetrics _metrics;
    private readonly IObservabilityGaugeSource _source;

    public GaugeRegistration(KnightMetrics metrics, IObservabilityGaugeSource source)
    {
        _metrics = metrics;
        _source = source;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _metrics.RegisterGauges(_source);

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>
/// Runs the retention sweep on a timer.
///
/// Separate from the fleet monitor and the observability worker even though all
/// three are periodic, because this one deletes. A sweep that trims a year of
/// rows should not share a schedule — or a failure mode — with the thing that
/// decides a server is offline.
/// </summary>
internal sealed class RetentionWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<RetentionWorker> _logger;
    private readonly RetentionOptions _options;

    public RetentionWorker(
        IServiceScopeFactory scopes,
        ILogger<RetentionWorker> logger,
        IOptions<RetentionOptions> options)
    {
        _scopes = scopes;
        _logger = logger;
        _options = options.Value;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            _logger.LogWarning(
                "Retention is disabled. The telemetry tables will grow without bound until it is switched back on.");

            return;
        }

        _logger.LogInformation("Retention sweep started; running every {Interval}.", _options.Interval);

        using var timer = new PeriodicTimer(_options.Interval);

        do
        {
            try
            {
                using var scope = _scopes.CreateScope();

                // Platform scope: retention is about every customer's rows, and
                // an unresolved scope would fail closed and delete nothing at all
                // — silently, and forever.
                scope.ServiceProvider
                    .GetRequiredService<Knight.Application.Abstractions.ControlPlane.ICustomerScopeAccessor>()
                    .SetPlatformScope();

                await scope.ServiceProvider
                    .GetRequiredService<IRetentionService>()
                    .ApplyAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "The retention sweep failed; it will run again next interval.");
            }
        }
        while (await SafeWaitAsync(timer, stoppingToken));
    }

    private static async Task<bool> SafeWaitAsync(PeriodicTimer timer, CancellationToken stoppingToken)
    {
        try
        {
            return await timer.WaitForNextTickAsync(stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
}
