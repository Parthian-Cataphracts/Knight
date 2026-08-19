using Knight.Application.Abstractions.ControlPlane;
using Knight.Infrastructure.ControlPlane.Integration;
using Microsoft.Extensions.Options;
using Stores;
using Stores.Domain;

namespace Knight.Api.BackgroundServices;

/// <summary>
/// Asks stores how they are, on a schedule (docs/store-integration.md §2, step 4).
///
/// KNIGHT polls rather than relying only on heartbeats because the interesting
/// failure is the one where the store cannot tell us anything: a store that has
/// stopped is exactly the store that stops sending heartbeats, and silence is
/// indistinguishable from a quiet night unless somebody asks.
///
/// The loop is deliberately unclever. One pass per interval, a bounded batch,
/// oldest contact first, and every store polled inside its own scope so one
/// failure cannot abort the pass or leak a customer scope into the next store.
/// Concurrency, backpressure and per-server fan-out limits belong to phase 4,
/// where the agent gives us a better signal than an HTTP GET.
/// </summary>
public sealed class StoreHealthPoller : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<StoreHealthPoller> _logger;
    private readonly StoreProbeOptions _options;

    public StoreHealthPoller(
        IServiceScopeFactory scopes,
        ILogger<StoreHealthPoller> logger,
        IOptions<StoreProbeOptions> options)
    {
        _scopes = scopes;
        _logger = logger;
        _options = options.Value;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.PollingEnabled)
        {
            _logger.LogInformation("Store health polling is disabled by configuration.");
            return;
        }

        var interval = TimeSpan.FromSeconds(_options.PollIntervalSeconds);
        using var timer = new PeriodicTimer(interval);

        _logger.LogInformation(
            "Store health polling every {IntervalSeconds}s, up to {BatchSize} stores per pass",
            _options.PollIntervalSeconds,
            _options.PollBatchSize);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await PollAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                // A failed pass must never take the poller down with it: the next
                // interval is a perfectly good time to try again.
                _logger.LogError(exception, "A store health polling pass failed");
            }

            if (!await timer.WaitForNextTickAsync(stoppingToken))
            {
                return;
            }
        }
    }

    private async Task PollAsync(CancellationToken cancellationToken)
    {
        PollTarget[] targets;

        using (var scope = _scopes.CreateScope())
        {
            // Platform scope: the poller acts for the platform, not for a
            // customer, and the isolation filter would otherwise hand it nothing.
            scope.ServiceProvider.GetRequiredService<ICustomerScopeAccessor>().SetPlatformScope();

            var stores = await scope.ServiceProvider
                .GetRequiredService<IStoreRepository>()
                .ListForHealthPollingAsync(_options.PollBatchSize, cancellationToken);

            // Projected out of the scope that loaded them: the entities belong to
            // a context that is about to be disposed, and each store is then
            // polled and recorded in a scope of its own.
            targets = stores
                .Select(store => new PollTarget(store.Id, store.PrimaryDomain, store.Environment))
                .ToArray();
        }

        foreach (var target in targets)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            await PollOneAsync(target, cancellationToken);
        }
    }

    private async Task PollOneAsync(PollTarget target, CancellationToken cancellationToken)
    {
        try
        {
            using var scope = _scopes.CreateScope();
            scope.ServiceProvider.GetRequiredService<ICustomerScopeAccessor>().SetPlatformScope();

            var probe = await scope.ServiceProvider
                .GetRequiredService<IStoreHealthProbe>()
                .ProbeAsync(target.Domain, cancellationToken);

            var status = Enum.TryParse<StoreHealthStatus>(probe.Status, ignoreCase: true, out var parsed)
                ? parsed
                : StoreHealthStatus.Unhealthy;

            var detail = probe.Detail;

            // Something answered, but said it is a different environment. That is
            // not a healthy store: either the domain now points somewhere else or
            // the store is misconfigured, and both are exactly what environment
            // binding exists to catch (docs/store-integration.md §6).
            if (status is not StoreHealthStatus.Unreachable
                && probe.ReportedEnvironment is { Length: > 0 } reported
                && !string.Equals(reported, target.Environment.ToString(), StringComparison.OrdinalIgnoreCase))
            {
                status = StoreHealthStatus.Unhealthy;
                detail = $"The host answered as '{reported}' but the store is registered as '{target.Environment}'.";
            }

            await scope.ServiceProvider
                .GetRequiredService<IStoreIntegrationService>()
                .RecordProbeAsync(
                    target.StoreId,
                    status,
                    probe.LatencyMs,
                    probe.ReportedVersion,
                    probe.DependenciesJson,
                    probe.FeaturesJson,
                    detail,
                    cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Could not record a health observation for store {StoreId}", target.StoreId);
        }
    }

    /// <summary>What one pass needs to know about a store, detached from the context that read it.</summary>
    private sealed record PollTarget(Guid StoreId, string Domain, StoreEnvironment Environment);
}
