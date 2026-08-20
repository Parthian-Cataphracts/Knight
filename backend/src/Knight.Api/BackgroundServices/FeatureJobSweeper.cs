using FeatureDelivery;
using Microsoft.Extensions.Options;

namespace Knight.Api.BackgroundServices;

/// <summary>
/// Returns abandoned installation jobs to the queue.
///
/// An agent that dies mid-install — the box rebooted, the process was killed,
/// the network partitioned — leaves a job nobody will ever report on. Without
/// this, that job holds the store's queue forever and every later install for
/// that store silently never happens. The claim deadline is what makes the
/// failure detectable; this is what acts on it.
///
/// It sweeps rather than schedules per job. A timer per claim would be more
/// precise and would also mean thousands of timers and a scheduler to lose track
/// of them; a periodic scan of the few jobs that are actually running is enough
/// and has no state of its own to get wrong.
///
/// Every failure is caught and logged. A background service that throws is a
/// background service that stops running, and the whole point of this one is that
/// it is still running weeks later when something finally goes wrong.
/// </summary>
public sealed class FeatureJobSweeper : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<FeatureJobSweeper> _logger;
    private readonly FeatureDeliveryOptions _options;

    public FeatureJobSweeper(
        IServiceScopeFactory scopes,
        ILogger<FeatureJobSweeper> logger,
        IOptions<FeatureDeliveryOptions> options)
    {
        _scopes = scopes;
        _logger = logger;
        _options = options.Value;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "Feature job sweeper started; checking every {Interval} for claims older than {Timeout}.",
            _options.JobSweepInterval,
            _options.JobClaimTimeout);

        using var timer = new PeriodicTimer(_options.JobSweepInterval);

        while (await SafeWaitAsync(timer, stoppingToken))
        {
            try
            {
            // One identity per pass, so everything this pass writes can be
            // tied back together — see BackgroundCorrelation.
                using var pass = BackgroundCorrelation.BeginPass("job claim sweep");

                using var scope = _scopes.CreateScope();

                // The sweep is platform work, not a customer's request, so it
                // runs in platform scope. Without this the isolation filter fails
                // closed and the sweep would find nothing at all — which would
                // look exactly like everything being healthy.
                scope.ServiceProvider
                    .GetRequiredService<Knight.Application.Abstractions.ControlPlane.ICustomerScopeAccessor>()
                    .SetPlatformScope();

                var jobs = scope.ServiceProvider.GetRequiredService<IAgentJobService>();
                var swept = await jobs.SweepExpiredClaimsAsync(stoppingToken);

                if (swept > 0)
                {
                    _logger.LogWarning("Recovered {Count} installation job(s) whose agent stopped reporting.", swept);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                // Logged and swallowed: one bad sweep must not end the service.
                _logger.LogError(exception, "The feature job sweep failed; it will run again next interval.");
            }
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
