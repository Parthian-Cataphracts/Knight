using Knight.Application.Abstractions.ControlPlane;
using Microsoft.Extensions.Options;
using Provisioning;

namespace Knight.Api.BackgroundServices;

/// <summary>
/// Moves provisioning runs along as the facts they wait for become true.
///
/// Almost every provisioning step waits for something that happens elsewhere and
/// tells nobody: an agent enrols, a store handshakes, an installation job
/// finishes, a domain is verified, a retention window closes. None of those
/// notifies the run that was waiting for it, and wiring every one of them to do
/// so would put provisioning knowledge into five modules that are better off
/// without it. A patient sweep re-asks instead, which is also what makes a run
/// survive the process restarting halfway through.
///
/// Every failure is caught and logged: a background service that throws is one
/// that has stopped running, and this one has to still be here in an hour when
/// the store finally comes up.
/// </summary>
public sealed class ProvisioningCoordinator : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<ProvisioningCoordinator> _logger;
    private readonly ProvisioningOptions _options;

    public ProvisioningCoordinator(
        IServiceScopeFactory scopes,
        ILogger<ProvisioningCoordinator> logger,
        IOptions<ProvisioningOptions> options)
    {
        _scopes = scopes;
        _logger = logger;
        _options = options.Value;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "Provisioning coordinator started; advancing unfinished runs every {Interval}.",
            _options.CoordinatorInterval);

        using var timer = new PeriodicTimer(_options.CoordinatorInterval);

        while (await SafeWaitAsync(timer, stoppingToken))
        {
            try
            {
                using var pass = BackgroundCorrelation.BeginPass("provisioning sweep");
                using var scope = _scopes.CreateScope();

                // Platform work rather than a customer's request. Without this
                // the isolation filter fails closed and the sweep would find
                // nothing — which looks exactly like there being nothing to do.
                scope.ServiceProvider
                    .GetRequiredService<ICustomerScopeAccessor>()
                    .SetPlatformScope();

                var provisioning = scope.ServiceProvider.GetRequiredService<IProvisioningService>();
                var advanced = await provisioning.AdvanceDueAsync(_options.CoordinatorBatchSize, stoppingToken);

                if (advanced > 0)
                {
                    _logger.LogInformation("{Count} provisioning run(s) moved to their next step.", advanced);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "The provisioning sweep failed; it will run again next interval.");
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
