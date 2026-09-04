using Knight.Application.Abstractions.ControlPlane;
using Provisioning;
using Provisioning.Domain;

namespace Knight.Api.BackgroundServices;

/// <summary>
/// Drives the simulated infrastructure adapter, so a self-service store provisions
/// itself with no operator and no real cloud (docs/self-service-saas-plan.md §11).
///
/// It mirrors what real infrastructure and a real agent would do out of band: on
/// each pass it finds the provisioning runs still in flight and, for each, asks
/// the adapter to produce whatever facts are still missing — a machine, an agent,
/// a credential, a verified domain, a handshake — and to apply the run's queued
/// delivery jobs. The provisioning engine is untouched; the ordinary
/// <see cref="IProvisioningService"/> then advances the run over the facts the
/// adapter just made true, exactly as it advances over facts an operator makes
/// true on a real deployment.
///
/// It stands down entirely when the configured adapter is not automated, so a real
/// deployment pays nothing for a simulator it does not use.
/// </summary>
public sealed class SimulatedInfrastructureWorker : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(3);

    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<SimulatedInfrastructureWorker> _logger;

    public SimulatedInfrastructureWorker(IServiceScopeFactory scopes, ILogger<SimulatedInfrastructureWorker> logger)
    {
        _scopes = scopes;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using (var probe = _scopes.CreateScope())
        {
            if (!probe.ServiceProvider.GetRequiredService<IInfrastructureAdapter>().IsAutomated)
            {
                _logger.LogInformation("Infrastructure is not simulated; the simulated worker is standing down.");
                return;
            }
        }

        _logger.LogWarning(
            "Simulated infrastructure is ENABLED: KNIGHT will fabricate servers, agents, credentials and handshakes. This is for development and demos only.");

        using var timer = new PeriodicTimer(Interval);

        while (await SafeWaitAsync(timer, stoppingToken))
        {
            try
            {
                await SweepAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "The simulated infrastructure sweep failed; it will run again next interval.");
            }
        }
    }

    private async Task SweepAsync(CancellationToken cancellationToken)
    {
        using var pass = BackgroundCorrelation.BeginPass("simulated infrastructure sweep");
        using var scope = _scopes.CreateScope();

        scope.ServiceProvider.GetRequiredService<ICustomerScopeAccessor>().SetPlatformScope();

        var provisioning = scope.ServiceProvider.GetRequiredService<IProvisioningService>();
        var adapter = scope.ServiceProvider.GetRequiredService<IInfrastructureAdapter>();

        var running = await provisioning.ListAsync(
            new ProvisioningJobQuery(1, 50, StoreId: null, CustomerId: null, State: ProvisioningState.Running),
            cancellationToken);

        foreach (var job in running.Items.Where(job => job.Kind is ProvisioningKind.Provision))
        {
            // Produce the facts still missing, then let the ordinary engine advance
            // the run over them. Both are idempotent, so a run part-way up simply
            // gets whatever it is still waiting for.
            await adapter.EnsureAsync(job.StoreId, cancellationToken);
            await provisioning.AdvanceAsync(job.Id, cancellationToken);
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
