using Knight.Application.Abstractions.ControlPlane;
using PlatformBilling;

namespace Knight.Api.BackgroundServices;

/// <summary>
/// Drives the activation outbox on a timer: the durable half of the payment →
/// provisioning handoff (hardening backlog P2). The webhook writes an intent in the
/// same transaction as the activation; this drains it, so a store is always
/// provisioned even if the process died between the two.
///
/// It runs in platform scope — it acts for every customer — and, like every
/// background service here, swallows and logs its failures so one bad entry cannot
/// stop the sweep.
/// </summary>
public sealed class OutboxDispatcherWorker : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(5);
    private const int BatchSize = 50;

    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<OutboxDispatcherWorker> _logger;

    public OutboxDispatcherWorker(IServiceScopeFactory scopes, ILogger<OutboxDispatcherWorker> logger)
    {
        _scopes = scopes;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);

        while (await SafeWaitAsync(timer, stoppingToken))
        {
            try
            {
                using var pass = BackgroundCorrelation.BeginPass("activation outbox sweep");
                using var scope = _scopes.CreateScope();
                scope.ServiceProvider.GetRequiredService<ICustomerScopeAccessor>().SetPlatformScope();

                var dispatched = await scope.ServiceProvider
                    .GetRequiredService<IActivationOutboxDispatcher>()
                    .DispatchDueAsync(BatchSize, stoppingToken);

                if (dispatched > 0)
                {
                    _logger.LogInformation("Dispatched {Count} activation(s) to provisioning.", dispatched);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "The activation outbox sweep failed; it will run again next interval.");
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
