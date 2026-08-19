using Knight.Application.Abstractions.ControlPlane;
using Microsoft.Extensions.Options;
using Servers;

namespace Knight.Api.BackgroundServices;

/// <summary>
/// Notices machines that have gone quiet, and keeps the metric table from growing
/// without end.
///
/// Both jobs are here rather than in two services because they share a shape:
/// periodic, platform-scoped, and pointless to run twice concurrently. They run
/// on different intervals — evaluation every minute, retention hourly — because
/// noticing an outage a minute late is bad and deleting month-old rows a minute
/// late is not.
///
/// This is the only thing in the system that can decide a server is offline.
/// Absence cannot be reported by the thing that is absent, so it takes something
/// that runs whether or not anybody checks in (docs/observability.md section 8).
/// </summary>
public sealed class FleetMonitor : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<FleetMonitor> _logger;
    private readonly ServerOptions _options;

    public FleetMonitor(IServiceScopeFactory scopes, ILogger<FleetMonitor> logger, IOptions<ServerOptions> options)
    {
        _scopes = scopes;
        _logger = logger;
        _options = options.Value;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "Fleet monitor started; evaluating every {Interval}, retaining metrics for {Retention}.",
            _options.EvaluationInterval,
            _options.MetricRetention);

        using var timer = new PeriodicTimer(_options.EvaluationInterval);
        var lastRetentionSweep = DateTimeOffset.MinValue;

        while (await SafeWaitAsync(timer, stoppingToken))
        {
            await RunAsync("fleet evaluation", stoppingToken, async monitoring =>
            {
                var changed = await monitoring.EvaluateAsync(stoppingToken);

                if (changed > 0)
                {
                    _logger.LogWarning("Fleet evaluation changed the state of {Count} server(s) or agent(s).", changed);
                }
            });

            if (DateTimeOffset.UtcNow - lastRetentionSweep < _options.RetentionInterval)
            {
                continue;
            }

            lastRetentionSweep = DateTimeOffset.UtcNow;

            await RunAsync("metric retention", stoppingToken, async monitoring =>
            {
                var deleted = await monitoring.ApplyRetentionAsync(stoppingToken);

                if (deleted > 0)
                {
                    _logger.LogInformation("Purged {Count} metric sample(s) past their retention window.", deleted);
                }
            });
        }
    }

    /// <summary>
    /// Runs one pass in its own scope, and swallows whatever it throws.
    ///
    /// A background service that throws is a background service that stops
    /// running, and the whole point of this one is that it is still running weeks
    /// later when something finally breaks.
    /// </summary>
    private async Task RunAsync(string what, CancellationToken stoppingToken, Func<IMonitoringService, Task> work)
    {
        try
        {
            using var scope = _scopes.CreateScope();

            // Platform scope: this is not a customer's request. Without it the
            // isolation filter fails closed and the sweep would find nothing at
            // all — which would look exactly like a perfectly healthy fleet.
            scope.ServiceProvider.GetRequiredService<ICustomerScopeAccessor>().SetPlatformScope();

            await work(scope.ServiceProvider.GetRequiredService<IMonitoringService>());
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "The {What} pass failed; it will run again next interval.", what);
        }
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
