using Knight.Application.Abstractions.ControlPlane;
using Microsoft.Extensions.Options;
using Observability;

namespace Knight.Api.BackgroundServices;

/// <summary>
/// Runs the rules nobody can evaluate at the moment something happens, and
/// drains the notification queue.
///
/// The two jobs live in one service because they are two halves of the same
/// sentence — notice that something is wrong, then tell somebody — and because
/// running either one twice concurrently would be pointless. They run on
/// different intervals: rules every minute, dispatch every twenty seconds,
/// because noticing a problem a minute late is acceptable and sitting on a
/// queued page for a minute is not.
///
/// Like the fleet monitor, this exists because absence and persistence cannot be
/// reported by an event: nothing pushes "my entitlement was never installed" or
/// "this rate has been climbing for ten minutes". Only something that runs on a
/// timer whether or not anybody is looking can see those.
/// </summary>
public sealed class ObservabilityWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<ObservabilityWorker> _logger;
    private readonly ObservabilityOptions _options;

    public ObservabilityWorker(
        IServiceScopeFactory scopes,
        ILogger<ObservabilityWorker> logger,
        IOptions<ObservabilityOptions> options)
    {
        _scopes = scopes;
        _logger = logger;
        _options = options.Value;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "Observability worker started; evaluating rules every {Rules}, dispatching every {Dispatch}.",
            _options.EvaluationInterval,
            _options.DispatchInterval);

        using var timer = new PeriodicTimer(_options.DispatchInterval);
        var lastEvaluation = DateTimeOffset.MinValue;

        while (await SafeWaitAsync(timer, stoppingToken))
        {
            await RunAsync("notification dispatch", stoppingToken, async provider =>
            {
                var result = await provider
                    .GetRequiredService<INotificationService>()
                    .DispatchDueAsync(stoppingToken);

                if (result.Attempted > 0)
                {
                    _logger.LogInformation(
                        "Dispatched {Delivered} of {Attempted} notification(s); {Failed} failed, {Disabled} channel(s) disabled.",
                        result.Delivered,
                        result.Attempted,
                        result.Failed,
                        result.ChannelsDisabled);
                }
            });

            if (DateTimeOffset.UtcNow - lastEvaluation < _options.EvaluationInterval)
            {
                continue;
            }

            lastEvaluation = DateTimeOffset.UtcNow;

            await RunAsync("rule evaluation", stoppingToken, async provider =>
            {
                var result = await provider
                    .GetRequiredService<IObservabilityRuleEvaluator>()
                    .EvaluateAsync(stoppingToken);

                if (result.Total > 0)
                {
                    _logger.LogWarning(
                        "Observability rules raised {Total} new alert(s): {Spikes} spike(s), {Failed} failed install(s), " +
                        "{Missing} entitled-but-not-installed, {Drifted} drifted, {Stuck} stuck job(s).",
                        result.Total,
                        result.Spikes,
                        result.FailedInstalls,
                        result.EntitledNotInstalled,
                        result.Drifted,
                        result.StuckJobs);
                }
            });
        }
    }

    /// <summary>
    /// Runs one pass in its own scope and swallows whatever it throws.
    ///
    /// A background service that throws is a background service that stops
    /// running, and the whole point of this one is that it is still running weeks
    /// later when something finally breaks.
    /// </summary>
    private async Task RunAsync(string what, CancellationToken stoppingToken, Func<IServiceProvider, Task> work)
    {
        try
        {
            // One identity per pass, so everything this pass writes can be
            // tied back together — see BackgroundCorrelation.
            using var pass = BackgroundCorrelation.BeginPass(what);

            using var scope = _scopes.CreateScope();

            // Platform scope: this is not a customer's request. Without it the
            // isolation filter fails closed and every sweep would find nothing at
            // all — which looks exactly like a perfectly healthy system.
            scope.ServiceProvider.GetRequiredService<ICustomerScopeAccessor>().SetPlatformScope();

            await work(scope.ServiceProvider);
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
