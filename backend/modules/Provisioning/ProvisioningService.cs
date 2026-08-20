using Knight.Application.Abstractions.ControlPlane;
using Knight.Application.Abstractions.Identity;
using Knight.Application.Abstractions.Time;
using Knight.Application.Exceptions;
using Microsoft.Extensions.Logging;
using Provisioning.Domain;

namespace Provisioning;

/// <summary>
/// The provisioning coordinator.
///
/// Every step is evaluated against a fact somebody else recorded: the store has
/// a usable credential, an agent enrolled on its server, the handshake happened,
/// the installation jobs finished, the store reported healthy. Nothing here
/// asserts progress on its own authority, which is what lets a run be resumed by
/// a different process an hour later and reach the same conclusion.
///
/// The one thing this service does decide is when a store becomes Active — and
/// only after the health-check step passed, never on an operator's word
/// (docs/store-provisioning.md §4).
/// </summary>
internal sealed class ProvisioningService : IProvisioningService
{
    private const int MaxPageSize = 100;

    private readonly IProvisioningJobRepository _jobs;
    private readonly IStoreProvisioningPort _stores;
    private readonly IServerProvisioningPort _servers;
    private readonly IBaseFeatureInstaller _features;
    private readonly IStoreDataPurger _purger;
    private readonly IRetentionPolicyReader _retention;
    private readonly IAuditTrail _audit;
    private readonly IDateTimeProvider _clock;
    private readonly ICurrentUser _currentUser;
    private readonly ILogger<ProvisioningService> _logger;

    public ProvisioningService(
        IProvisioningJobRepository jobs,
        IStoreProvisioningPort stores,
        IServerProvisioningPort servers,
        IBaseFeatureInstaller features,
        IStoreDataPurger purger,
        IRetentionPolicyReader retention,
        IAuditTrail audit,
        IDateTimeProvider clock,
        ICurrentUser currentUser,
        ILogger<ProvisioningService> logger)
    {
        _jobs = jobs;
        _stores = stores;
        _servers = servers;
        _features = features;
        _purger = purger;
        _retention = retention;
        _audit = audit;
        _clock = clock;
        _currentUser = currentUser;
        _logger = logger;
    }

    public Task<ProvisioningJob> StartProvisioningAsync(Guid storeId, string? idempotencyKey, CancellationToken cancellationToken) =>
        StartAsync(storeId, ProvisioningKind.Provision, idempotencyKey, cancellationToken);

    public Task<ProvisioningJob> StartDeprovisioningAsync(Guid storeId, string? idempotencyKey, CancellationToken cancellationToken) =>
        StartAsync(storeId, ProvisioningKind.Deprovision, idempotencyKey, cancellationToken);

    private async Task<ProvisioningJob> StartAsync(
        Guid storeId,
        ProvisioningKind kind,
        string? idempotencyKey,
        CancellationToken cancellationToken)
    {
        var store = await _stores.GetAsync(storeId, cancellationToken)
            ?? throw new NotFoundException($"Store '{storeId}' was not found.");

        var key = string.IsNullOrWhiteSpace(idempotencyKey)
            ? $"{kind}:{storeId}"
            : idempotencyKey.Trim();

        if (await _jobs.FindByIdempotencyKeyAsync(storeId, key, cancellationToken) is { } existing)
        {
            return existing;
        }

        // Two runs against one store would race each other into the same
        // credentials, the same install jobs and, in the deprovisioning case,
        // one purging what the other is exporting.
        if (await _jobs.FindActiveForStoreAsync(storeId, cancellationToken) is { } active)
        {
            throw new ConflictException(
                $"Store '{store.Name}' already has an unfinished {active.Kind} job. Finish or cancel it first.");
        }

        var now = _clock.UtcNow;

        var job = ProvisioningJob.Start(
            Guid.CreateVersion7(),
            now,
            store.CustomerId,
            storeId,
            kind,
            key,
            Guid.CreateVersion7().ToString("n"),
            _currentUser.UserId ?? Guid.Empty);

        if (kind is ProvisioningKind.Deprovision)
        {
            var window = await _retention.ResolveAsync(store.CustomerId, cancellationToken);
            job.RetainDataUntil(now.Add(window), now);
        }

        await _jobs.AddAsync(job, cancellationToken);
        await _jobs.SaveChangesAsync(cancellationToken);

        await _audit.RecordAsync(
            kind is ProvisioningKind.Provision ? "store.provisioning.started" : "store.deprovisioning.started",
            nameof(ProvisioningJob),
            job.Id.ToString(),
            job.CustomerId,
            cancellationToken,
            newValue: new { job.StoreId, Kind = kind.ToString(), job.RetainUntil });

        return await AdvanceAsync(job, cancellationToken);
    }

    public async Task<ProvisioningJob> AdvanceAsync(Guid jobId, CancellationToken cancellationToken) =>
        await AdvanceAsync(await RequireAsync(jobId, cancellationToken), cancellationToken);

    public async Task<ProvisioningJob> CompleteManualStepAsync(
        Guid jobId,
        string stepName,
        string? detail,
        CancellationToken cancellationToken)
    {
        var job = await RequireAsync(jobId, cancellationToken);
        var now = _clock.UtcNow;
        var actor = _currentUser.UserId ?? Guid.Empty;

        var created = job.CompleteManualStep(stepName, actor, detail, now);
        if (created is not null)
        {
            _jobs.RegisterNewStep(created);
        }

        await _jobs.SaveChangesAsync(cancellationToken);

        await _audit.RecordAsync(
            "store.provisioning.step.completed",
            nameof(ProvisioningJob),
            job.Id.ToString(),
            job.CustomerId,
            cancellationToken,
            newValue: new { job.StoreId, step = stepName, detail });

        return await AdvanceAsync(job, cancellationToken);
    }

    public async Task<ProvisioningJob> RetryAsync(Guid jobId, CancellationToken cancellationToken)
    {
        var job = await RequireAsync(jobId, cancellationToken);

        job.Retry(_clock.UtcNow);
        await _jobs.SaveChangesAsync(cancellationToken);

        await _audit.RecordAsync(
            "store.provisioning.retried",
            nameof(ProvisioningJob),
            job.Id.ToString(),
            job.CustomerId,
            cancellationToken,
            newValue: new { job.StoreId });

        return await AdvanceAsync(job, cancellationToken);
    }

    public async Task<ProvisioningJob> CancelAsync(Guid jobId, string reason, CancellationToken cancellationToken)
    {
        var job = await RequireAsync(jobId, cancellationToken);

        job.Cancel(reason, _clock.UtcNow);
        await _jobs.SaveChangesAsync(cancellationToken);

        await _audit.RecordAsync(
            "store.provisioning.cancelled",
            nameof(ProvisioningJob),
            job.Id.ToString(),
            job.CustomerId,
            cancellationToken,
            newValue: new { job.StoreId, reason });

        return job;
    }

    public Task<ProvisioningJob?> GetAsync(Guid jobId, CancellationToken cancellationToken) =>
        _jobs.GetByIdAsync(jobId, cancellationToken);

    public async Task<ProvisioningJobPage> ListAsync(ProvisioningJobQuery query, CancellationToken cancellationToken)
    {
        var page = query.Page < 1 ? 1 : query.Page;
        var pageSize = query.PageSize is < 1 or > MaxPageSize ? 25 : query.PageSize;

        var (items, total) = await _jobs.ListAsync(page, pageSize, query.StoreId, query.CustomerId, query.State, cancellationToken);
        return new ProvisioningJobPage(items, page, pageSize, total);
    }

    public async Task<int> AdvanceDueAsync(int limit, CancellationToken cancellationToken)
    {
        var due = await _jobs.ListAdvanceableAsync(limit, cancellationToken);
        var advanced = 0;

        foreach (var job in due)
        {
            var before = job.NextStep();

            try
            {
                await AdvanceAsync(job, cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // One store that cannot be evaluated must not stop the sweep for
                // every other store waiting to come up.
                _logger.LogError(
                    exception,
                    "Provisioning job {JobId} for store {StoreId} could not be advanced.",
                    job.Id,
                    job.StoreId);

                continue;
            }

            if (!string.Equals(before, job.NextStep(), StringComparison.Ordinal))
            {
                advanced++;
            }
        }

        return advanced;
    }

    /// <summary>
    /// Evaluates steps in order until one of them does not finish.
    ///
    /// The loop is bounded by the pipeline: every pass either finishes a step or
    /// stops, so a store whose agent has not enrolled costs one evaluation, not
    /// a spin.
    /// </summary>
    private async Task<ProvisioningJob> AdvanceAsync(ProvisioningJob job, CancellationToken cancellationToken)
    {
        if (job.IsFinished && job.State is not ProvisioningState.Failed)
        {
            return job;
        }

        var store = await _stores.GetAsync(job.StoreId, cancellationToken);
        if (store is null)
        {
            // The store record is gone. For a deprovisioning run that is the
            // destination; for a provisioning run there is nothing left to build.
            if (!job.IsFinished)
            {
                job.Fail("provisioning.store.missing", "The store record no longer exists.", _clock.UtcNow);
                await _jobs.SaveChangesAsync(cancellationToken);
            }

            return job;
        }

        var now = _clock.UtcNow;
        var progressed = false;

        while (job.NextStep() is { } stepName && !job.IsFinished)
        {
            var definition = ProvisioningPipeline.Require(job.Kind, stepName);
            StepOutcome outcome;

            try
            {
                outcome = await EvaluateAsync(job, definition, store, cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                outcome = StepOutcome.Failed("provisioning.step.error", exception.Message);
            }

            var created = job.ReportStep(
                stepName,
                outcome.Status,
                now,
                outcome.Detail,
                outcome.ErrorCode);

            if (created is not null)
            {
                _jobs.RegisterNewStep(created);
            }

            progressed = true;

            if (outcome.Status is not (ProvisioningStepStatus.Succeeded or ProvisioningStepStatus.Skipped))
            {
                break;
            }

            // Later steps read facts the step that just finished may have
            // changed — activating the store is the obvious one.
            store = await _stores.GetAsync(job.StoreId, cancellationToken) ?? store;
        }

        if (progressed)
        {
            await _jobs.SaveChangesAsync(cancellationToken);
        }

        if (job.State is ProvisioningState.Succeeded)
        {
            await _audit.RecordAsync(
                job.Kind is ProvisioningKind.Provision ? "store.provisioning.completed" : "store.deprovisioning.completed",
                nameof(ProvisioningJob),
                job.Id.ToString(),
                job.CustomerId,
                cancellationToken,
                newValue: new { job.StoreId, job.BaseImageVersion });
        }

        return job;
    }

    private async Task<StepOutcome> EvaluateAsync(
        ProvisioningJob job,
        ProvisioningStepDefinition definition,
        ProvisioningStoreSnapshot store,
        CancellationToken cancellationToken) => definition.Name switch
    {
        // --- Provisioning ----------------------------------------------------
        ProvisioningPipeline.Server => store.ServerId is { } serverId
            ? StepOutcome.Succeeded($"Recorded on server {serverId}.")
            : StepOutcome.Waiting("No machine is recorded for this store yet. Register the server and assign it to the store."),

        // A store that has completed a handshake demonstrably exists and is
        // running, which is the only evidence of this step KNIGHT can gather on
        // its own while instance creation is manual.
        ProvisioningPipeline.Instance => store.HasHandshaked
            ? StepOutcome.Succeeded("The store instance answered a handshake.")
            : StepOutcome.Waiting("Waiting for a Django instance built from the base store image."),

        ProvisioningPipeline.StoreRecord => StepOutcome.Succeeded(
            $"Registered as '{store.Slug}' in {store.Environment}, hosting {store.HostingModel}."),

        ProvisioningPipeline.Credentials => store.HasUsableCredential
            ? StepOutcome.Succeeded("The store holds a usable credential.")
            : StepOutcome.Waiting(
                "No usable credential. Issue one from the store's credentials page — the secret is shown once and is never stored."),

        ProvisioningPipeline.Agent => store.ServerId is not { } server
            ? StepOutcome.Waiting("The store has no server, so no agent can be enrolled for it.")
            : await _servers.HasEnrolledAgentAsync(server, cancellationToken)
                ? StepOutcome.Succeeded("An enrolled agent is running on the store's server.")
                : StepOutcome.Waiting("Waiting for the agent on the store's server to enrol."),

        ProvisioningPipeline.BaseFeatures => Describe(
            await _features.EnsureBaseFeaturesAsync(job.StoreId, cancellationToken)),

        ProvisioningPipeline.Configuration => store.HasHandshaked
            ? StepOutcome.Succeeded("The store has handshaked and holds its configuration.")
            : StepOutcome.Waiting("Waiting for the store's first handshake."),

        ProvisioningPipeline.DomainAndTls => store.IsDomainVerified
            ? StepOutcome.Succeeded($"Ownership of {store.PrimaryDomain} is proven.")
            : StepOutcome.Waiting($"Point {store.PrimaryDomain} at the store, serve TLS, and verify ownership."),

        ProvisioningPipeline.HealthCheck => await CompleteHealthCheckAsync(store, cancellationToken),

        // --- Deprovisioning --------------------------------------------------
        ProvisioningPipeline.DisableFeatures => StepOutcome.Succeeded(
            $"{await _features.DisableAllAsync(job.StoreId, "The store is being deprovisioned.", cancellationToken)} Features disabled."),

        ProvisioningPipeline.RevokeAccess => await RevokeAccessAsync(store, cancellationToken),

        ProvisioningPipeline.StopIngestion => string.Equals(store.Status, "Archived", StringComparison.Ordinal)
            ? StepOutcome.Succeeded("The store is archived; nothing it sends is accepted any more.")
            : StepOutcome.Failed("provisioning.ingestion.open", "The store is not archived, so it can still report to KNIGHT."),

        ProvisioningPipeline.Retain => job.RetainUntil is { } retainUntil && _clock.UtcNow < retainUntil
            ? StepOutcome.Waiting($"The store's data is retained until {retainUntil:u}.")
            : StepOutcome.Succeeded("The retention window has closed."),

        ProvisioningPipeline.Export => StepOutcome.Waiting(
            "Produce the exportable backup and hand it to the customer before anything is purged."),

        ProvisioningPipeline.Purge => Describe(await _purger.PurgeAsync(job.StoreId, cancellationToken)),

        _ => StepOutcome.Failed("provisioning.step.unknown", $"Step '{definition.Name}' has no evaluation."),
    };

    private async Task<StepOutcome> CompleteHealthCheckAsync(
        ProvisioningStoreSnapshot store,
        CancellationToken cancellationToken)
    {
        if (!store.IsHealthy)
        {
            return StepOutcome.Waiting("Waiting for the store to report healthy. A store never becomes Active without one.");
        }

        if (string.Equals(store.Status, "Active", StringComparison.Ordinal))
        {
            return StepOutcome.Succeeded("The store is healthy and Active.");
        }

        await _stores.ActivateAsync(store.StoreId, cancellationToken);

        await _audit.RecordAsync(
            "store.activated",
            "Store",
            store.StoreId.ToString(),
            store.CustomerId,
            cancellationToken,
            newValue: new { reason = "provisioning health check passed" });

        return StepOutcome.Succeeded("The store passed its health check and is now Active.");
    }

    private async Task<StepOutcome> RevokeAccessAsync(ProvisioningStoreSnapshot store, CancellationToken cancellationToken)
    {
        await _stores.ArchiveAsync(store.StoreId, cancellationToken);

        var agents = 0;

        // Only for a machine dedicated to this customer. Revoking the agents on
        // shared hardware would take out every other store on the box.
        if (store.ServerId is { } serverId &&
            string.Equals(store.HostingModel, "DedicatedManaged", StringComparison.Ordinal))
        {
            agents = await _servers.RevokeAgentsAsync(serverId, "The store was deprovisioned.", cancellationToken);
        }

        return StepOutcome.Succeeded(
            agents > 0
                ? $"Credentials revoked and {agents} agent(s) on the dedicated server revoked."
                : "Store credentials revoked.");
    }

    private static StepOutcome Describe(BaseFeatureProgress progress)
    {
        if (progress.Failed > 0)
        {
            return StepOutcome.Failed(
                "provisioning.features.failed",
                progress.Detail ?? $"{progress.Failed} of {progress.Total} Feature installations failed.");
        }

        return progress.IsComplete
            ? StepOutcome.Succeeded($"{progress.Completed} of {progress.Total} entitled Features installed.")
            : StepOutcome.Waiting($"{progress.Completed} of {progress.Total} entitled Features installed so far.");
    }

    private static StepOutcome Describe(PurgeSummary summary) =>
        StepOutcome.Succeeded(
            $"{summary.Total} records deleted: {summary.ErrorEvents} errors, {summary.LogEntries} log entries, " +
            $"{summary.LifecycleEvents} events, {summary.HealthChecks} health checks, {summary.Deployments} deployments, " +
            $"{summary.Backups} backup reports.");

    private async Task<ProvisioningJob> RequireAsync(Guid jobId, CancellationToken cancellationToken) =>
        await _jobs.GetByIdAsync(jobId, cancellationToken)
        ?? throw new NotFoundException($"Provisioning job '{jobId}' was not found.");

    private readonly record struct StepOutcome(ProvisioningStepStatus Status, string? Detail, string? ErrorCode)
    {
        public static StepOutcome Succeeded(string detail) => new(ProvisioningStepStatus.Succeeded, detail, null);

        public static StepOutcome Waiting(string detail) => new(ProvisioningStepStatus.Waiting, detail, null);

        public static StepOutcome Failed(string code, string detail) => new(ProvisioningStepStatus.Failed, detail, code);
    }
}
