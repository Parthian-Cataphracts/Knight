using FeatureDelivery;
using Knight.Application.Abstractions.ControlPlane;
using Microsoft.Extensions.Options;

namespace Knight.Api.BackgroundServices;

/// <summary>
/// Sends out the next wave of a staged rollout once the wave before it has
/// finished.
///
/// Most of the time this has nothing to do, because the usual path is faster:
/// an agent reports a job, <c>AgentJobService.CompleteAsync</c> records the
/// result against the rollout, and a failed canary halts it at that moment. This
/// sweep exists for the wave that becomes ready without anybody being there to
/// notice — the last store in a wave reporting while the process was restarting,
/// or a rollout resumed by an operator whose request finished before the wave
/// was dispatchable.
///
/// Every failure is caught and logged. A background service that throws is one
/// that has stopped running, and a rollout stuck halfway across a fleet is
/// exactly the state nobody wants to be quietly in.
/// </summary>
public sealed class RolloutCoordinator : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<RolloutCoordinator> _logger;
    private readonly FeatureDeliveryOptions _options;

    public RolloutCoordinator(
        IServiceScopeFactory scopes,
        ILogger<RolloutCoordinator> logger,
        IOptions<FeatureDeliveryOptions> options)
    {
        _scopes = scopes;
        _logger = logger;
        _options = options.Value;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "Rollout coordinator started; checking for dispatchable waves every {Interval}.",
            _options.JobSweepInterval);

        using var timer = new PeriodicTimer(_options.JobSweepInterval);

        while (await SafeWaitAsync(timer, stoppingToken))
        {
            try
            {
                using var pass = BackgroundCorrelation.BeginPass("rollout sweep");
                using var scope = _scopes.CreateScope();

                // A rollout spans customers, so this is platform work. Without an
                // explicit scope the isolation filter fails closed and the sweep
                // finds nothing, which looks exactly like there being nothing to
                // do.
                scope.ServiceProvider
                    .GetRequiredService<ICustomerScopeAccessor>()
                    .SetPlatformScope();

                var rollouts = scope.ServiceProvider.GetRequiredService<IFeatureRolloutService>();
                var dispatched = await rollouts.AdvanceAllAsync(stoppingToken);

                if (dispatched > 0)
                {
                    _logger.LogInformation("{Count} rollout wave(s) dispatched.", dispatched);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "The rollout sweep failed; it will run again next interval.");
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
