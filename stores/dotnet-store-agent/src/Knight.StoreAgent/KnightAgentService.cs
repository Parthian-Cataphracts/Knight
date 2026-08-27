using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Knight.StoreAgent;

/// <summary>
/// Tells KNIGHT this store is alive, and what it runs.
///
/// The runtime block is not diagnostics. KNIGHT decides from it which
/// compatibility checks apply and refuses a Feature built for another runtime by
/// name; a store that never heartbeats is a store nothing can be delivered to,
/// and the refusal an operator sees is about compatibility rather than about the
/// missing report.
/// </summary>
public sealed class KnightHeartbeatService(
    KnightClient client,
    IOptions<KnightOptions> options,
    ILogger<KnightHeartbeatService> logger)
    : BackgroundService
{
    private readonly KnightOptions _options = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            logger.LogInformation("The KNIGHT agent is disabled; no heartbeats will be sent.");
            return;
        }

        var registry = new FeatureRegistry(_options.FeatureRoot);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var features = await registry.EnabledSlugsAsync(stoppingToken);
                await client.HeartbeatAsync("Healthy", features, null, null, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                // Logged and swallowed. A control plane that has gone away must
                // never take the shop down with it, and the next tick will try
                // again.
                logger.LogWarning(exception, "Heartbeat to KNIGHT failed.");
            }

            try
            {
                await Task.Delay(_options.HeartbeatInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }
}

/// <summary>
/// Asks KNIGHT for work, runs it, and reports what happened.
///
/// Outbound only: the store asks; KNIGHT never connects inward. One job at a
/// time and no concurrency — KNIGHT hands a job out already claimed so two
/// agents cannot hold the same one, and two jobs running side by side in one
/// store would be a race this store would lose for nothing.
/// </summary>
public sealed class KnightAgentService(
    KnightClient client,
    JobRunner runner,
    IOptions<KnightOptions> options,
    ILogger<KnightAgentService> logger)
    : BackgroundService
{
    private readonly KnightOptions _options = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            logger.LogInformation("The KNIGHT agent is disabled; no jobs will be claimed.");
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                while (await RunOneAsync(stoppingToken))
                {
                    // Straight on to the next: a store that has just been
                    // entitled to six Features should not wait six poll
                    // intervals to have them.
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Claiming work from KNIGHT failed.");
            }

            try
            {
                await Task.Delay(_options.PollInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    /// <summary>Runs one job if there is one. Returns whether there was.</summary>
    public async Task<bool> RunOneAsync(CancellationToken cancellationToken)
    {
        var job = await client.ClaimJobAsync(cancellationToken);

        if (job is null)
        {
            return false;
        }

        logger.LogInformation(
            "Running {Type} of {Slug} {Version} (job {JobId}).",
            job.Type,
            job.FeatureSlug,
            job.TargetVersion,
            job.JobId);

        var outcome = await runner.RunAsync(
            job,
            (step, token) => client.ReportStepAsync(
                job.JobId,
                step.Step,
                step.Status,
                step.Detail,
                step.Code,
                step.DurationMilliseconds,
                token),
            cancellationToken);

        if (outcome.Succeeded)
        {
            await client.CompleteJobAsync(
                job.JobId,
                succeeded: true,
                failureCode: null,
                failureMessage: null,
                installedVersion: outcome.InstalledVersion,
                health: "Healthy",
                cancellationToken);

            logger.LogInformation("{Slug} {Version} installed.", job.FeatureSlug, outcome.InstalledVersion);

            return true;
        }

        // Reported as a failure, with the step's own code. A job that failed
        // silently is a Feature a merchant has paid for and does not have, and a
        // job left in Running is one nobody knows is missing.
        await client.CompleteJobAsync(
            job.JobId,
            succeeded: false,
            failureCode: outcome.Code,
            failureMessage: outcome.Detail,
            installedVersion: null,
            health: "Unhealthy",
            cancellationToken);

        logger.LogError(
            "{Slug} failed at {Step}: {Code} — {Detail}",
            job.FeatureSlug,
            outcome.FailedStep,
            outcome.Code,
            outcome.Detail);

        return true;
    }
}
