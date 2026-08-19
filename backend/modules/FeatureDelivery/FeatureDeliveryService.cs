using System.Text.Json;
using FeatureDelivery.Domain;
using Knight.Application.Abstractions.ControlPlane;
using Knight.Application.Abstractions.Identity;
using Knight.Application.Abstractions.Security;
using Knight.Application.Abstractions.Time;
using Knight.Application.Exceptions;

namespace FeatureDelivery;

/// <summary>
/// The delivery engine's orchestration.
///
/// The shape of every write here is the same, and deliberately so: resolve, record
/// the installation row, queue a job, audit. Nothing in this class reaches into a
/// store. That is what makes an install requested while a store is unreachable a
/// queued job rather than a failed request, and it is why a store that has been
/// offline for a week catches up by polling rather than by anyone re-running
/// anything.
/// </summary>
internal sealed class FeatureDeliveryService : IFeatureDeliveryService
{
    private const int MaxPageSize = 100;

    private readonly IFeatureInstallationRepository _installations;
    private readonly IFeatureInstallationJobRepository _jobs;
    private readonly IFeatureConfigurationRepository _configurations;
    private readonly IFeaturePlanResolver _resolver;
    private readonly IStoreDeliveryReader _stores;
    private readonly ISecretProtector _secrets;
    private readonly IAuditTrail _audit;
    private readonly IDateTimeProvider _clock;
    private readonly ICurrentUser _currentUser;

    public FeatureDeliveryService(
        IFeatureInstallationRepository installations,
        IFeatureInstallationJobRepository jobs,
        IFeatureConfigurationRepository configurations,
        IFeaturePlanResolver resolver,
        IStoreDeliveryReader stores,
        ISecretProtector secrets,
        IAuditTrail audit,
        IDateTimeProvider clock,
        ICurrentUser currentUser)
    {
        _installations = installations;
        _jobs = jobs;
        _configurations = configurations;
        _resolver = resolver;
        _stores = stores;
        _secrets = secrets;
        _audit = audit;
        _clock = clock;
        _currentUser = currentUser;
    }

    public async Task<FeaturePlan> PreviewAsync(Guid storeId, string slug, string? versionRange, CancellationToken cancellationToken)
    {
        var context = await RequireContextAsync(storeId, cancellationToken);
        return await _resolver.ResolveAsync(slug, versionRange, context, cancellationToken);
    }

    public Task<InstallationRequestResult> InstallAsync(InstallFeatureInput input, CancellationToken cancellationToken)
        => RequestAsync(input, JobType.Install, cancellationToken);

    public Task<InstallationRequestResult> UpgradeAsync(InstallFeatureInput input, CancellationToken cancellationToken)
        => RequestAsync(input, JobType.Upgrade, cancellationToken);

    /// <summary>
    /// The one path both install and upgrade take.
    ///
    /// A plan can contain several steps — the Feature asked for plus whatever it
    /// depends on — and each actionable step becomes its own job, queued in the
    /// plan's topological order. One job per Feature rather than one job for the
    /// whole plan, because a plan that fails halfway has to leave behind an
    /// accurate record of which Features are installed and which are not.
    /// </summary>
    private async Task<InstallationRequestResult> RequestAsync(
        InstallFeatureInput input,
        JobType type,
        CancellationToken cancellationToken)
    {
        var now = _clock.UtcNow;
        var customerId = await RequireCustomerAsync(input.StoreId, cancellationToken);
        var context = await RequireContextAsync(input.StoreId, cancellationToken);

        var plan = await _resolver.ResolveAsync(input.Slug, input.VersionRange, context, cancellationToken);

        var root = plan.Steps.FirstOrDefault(step => step.IsRoot);
        var installation = await EnsureInstallationAsync(
            input.StoreId,
            customerId,
            root?.FeatureId,
            input.Slug,
            cancellationToken);

        if (!plan.IsSuccessful)
        {
            // The customer may well be entitled to this. Recording why it cannot
            // be installed — rather than throwing and leaving nothing behind — is
            // what stops "entitled but missing, nobody knows why" support tickets.
            installation.RecordBlockingReason(plan.DescribeFailures(), now);
            await _installations.SaveChangesAsync(cancellationToken);

            await _audit.RecordAsync(
                "feature.installation.blocked",
                "FeatureInstallation",
                installation.Id.ToString(),
                customerId,
                cancellationToken,
                newValue: new { input.Slug, Failures = plan.Failures });

            return new InstallationRequestResult(plan, [], installation);
        }

        var actionable = plan.ActionableSteps;
        if (actionable.Count == 0)
        {
            installation.RecordBlockingReason(null, now);
            await _installations.SaveChangesAsync(cancellationToken);
            return new InstallationRequestResult(plan, [], installation);
        }

        var queued = new List<FeatureInstallationJob>(actionable.Count);
        var baseKey = string.IsNullOrWhiteSpace(input.IdempotencyKey)
            ? $"{type}:{input.StoreId}:{input.Slug}:{now.ToUnixTimeMilliseconds()}"
            : input.IdempotencyKey.Trim();

        foreach (var step in actionable)
        {
            // Each step gets its own key derived from the caller's, so a retried
            // request matches every job it created the first time rather than
            // just the first one.
            var stepKey = $"{baseKey}:{step.Slug}";

            var existing = await _jobs.FindByIdempotencyKeyAsync(input.StoreId, stepKey, cancellationToken);
            if (existing is not null)
            {
                queued.Add(existing);
                continue;
            }

            var stepInstallation = step.Slug == installation.FeatureSlug
                ? installation
                : await EnsureInstallationAsync(input.StoreId, customerId, step.FeatureId, step.Slug, cancellationToken);

            if (!stepInstallation.CanAcceptJob)
            {
                throw new ConflictException(
                    $"'{step.Slug}' already has work in flight on this store; wait for it to finish or cancel it.");
            }

            var jobType = step.Action is FeaturePlanAction.Upgrade ? JobType.Upgrade : JobType.Install;

            var job = FeatureInstallationJob.Queue(
                Guid.CreateVersion7(),
                now,
                input.StoreId,
                customerId,
                stepInstallation.Id,
                step.FeatureId,
                step.Slug,
                jobType,
                step.VersionId,
                step.Version,
                stepKey,
                CorrelationId(),
                _currentUser.UserId ?? Guid.Empty,
                input.Trigger,
                traceParent: TraceParent());

            stepInstallation.QueueJob(job.Id, step.VersionId, step.Version, now);

            await _jobs.AddAsync(job, cancellationToken);
            queued.Add(job);
        }

        await _installations.SaveChangesAsync(cancellationToken);
        await _jobs.SaveChangesAsync(cancellationToken);

        await _audit.RecordAsync(
            $"feature.{type.ToString().ToLowerInvariant()}.requested",
            "FeatureInstallation",
            installation.Id.ToString(),
            customerId,
            cancellationToken,
            newValue: new
            {
                input.Slug,
                Steps = actionable.Select(step => new { step.Slug, step.Version, Action = step.Action.ToString() }),
            });

        return new InstallationRequestResult(plan, queued, installation);
    }

    public async Task<FeatureInstallationJob> DisableAsync(Guid storeId, Guid featureId, string reason, CancellationToken cancellationToken)
    {
        var installation = await RequireInstallationAsync(storeId, featureId, cancellationToken);

        if (installation.State is not InstallationState.Installed)
        {
            throw new ConflictException($"'{installation.FeatureSlug}' is not installed and running, so it cannot be disabled.");
        }

        return await QueueSimpleJobAsync(installation, JobType.Disable, reason, cancellationToken);
    }

    public async Task<FeatureInstallationJob> EnableAsync(Guid storeId, Guid featureId, CancellationToken cancellationToken)
    {
        var installation = await RequireInstallationAsync(storeId, featureId, cancellationToken);

        if (installation.State is not InstallationState.Disabled)
        {
            throw new ConflictException($"'{installation.FeatureSlug}' is not disabled, so there is nothing to enable.");
        }

        return await QueueSimpleJobAsync(installation, JobType.Enable, "Re-enabled.", cancellationToken);
    }

    public async Task<FeatureInstallationJob> UninstallAsync(Guid storeId, Guid featureId, CancellationToken cancellationToken)
    {
        var installation = await RequireInstallationAsync(storeId, featureId, cancellationToken);

        await EnsureNothingDependsOnAsync(installation, cancellationToken);

        var now = _clock.UtcNow;
        var job = await QueueSimpleJobAsync(installation, JobType.Uninstall, "Uninstall requested.", cancellationToken, save: false);

        installation.BeginUninstall(job.Id, now);

        await _installations.SaveChangesAsync(cancellationToken);
        await _jobs.SaveChangesAsync(cancellationToken);

        return job;
    }

    public async Task<FeatureInstallationJob> RollbackAsync(Guid storeId, Guid featureId, CancellationToken cancellationToken)
    {
        var installation = await RequireInstallationAsync(storeId, featureId, cancellationToken);

        if (installation.PreviousVersion is null)
        {
            throw new ConflictException(
                $"There is no earlier version of '{installation.FeatureSlug}' on this store to roll back to.");
        }

        return await QueueSimpleJobAsync(
            installation,
            JobType.Rollback,
            $"Rollback to {installation.PreviousVersion}.",
            cancellationToken);
    }

    public async Task<FeatureInstallationJob> ConfigureAsync(
        Guid storeId,
        Guid featureId,
        string valuesJson,
        IReadOnlyDictionary<string, string> secrets,
        CancellationToken cancellationToken)
    {
        var now = _clock.UtcNow;
        var installation = await RequireInstallationAsync(storeId, featureId, cancellationToken);

        if (!IsJsonObject(valuesJson))
        {
            throw new ValidationException(new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                ["values"] = ["Configuration values must be a JSON object."],
            });
        }

        // Secrets are sealed as one document rather than per value: the names are
        // needed in the clear so the dashboard can show what is set, and the
        // values are needed only as an opaque blob the install channel carries.
        var secretNames = secrets.Keys.Order(StringComparer.Ordinal).ToArray();
        var sealedSecrets = secrets.Count == 0
            ? null
            : _secrets.Protect(JsonSerializer.Serialize(secrets));

        var configuration = await _configurations.FindAsync(storeId, featureId, cancellationToken);

        if (configuration is null)
        {
            configuration = FeatureConfiguration.Create(
                Guid.CreateVersion7(),
                now,
                storeId,
                installation.CustomerId,
                featureId,
                valuesJson,
                sealedSecrets,
                JsonSerializer.Serialize(secretNames),
                _currentUser.UserId ?? Guid.Empty);

            await _configurations.AddAsync(configuration, cancellationToken);
        }
        else
        {
            configuration.Replace(
                valuesJson,
                sealedSecrets,
                JsonSerializer.Serialize(secretNames),
                _currentUser.UserId ?? Guid.Empty,
                now);
        }

        await _configurations.SaveChangesAsync(cancellationToken);

        var job = await QueueSimpleJobAsync(
            installation,
            JobType.ApplyConfiguration,
            $"Configuration version {configuration.Version}.",
            cancellationToken);

        // The audit records the names, never the values.
        await _audit.RecordAsync(
            "feature.configuration.updated",
            "FeatureConfiguration",
            configuration.Id.ToString(),
            installation.CustomerId,
            cancellationToken,
            newValue: new { installation.FeatureSlug, configuration.Version, SecretNames = secretNames });

        return job;
    }

    public async Task<InstallationPage> ListInstallationsAsync(
        int page,
        int pageSize,
        Guid? storeId,
        Guid? customerId,
        InstallationState? state,
        CancellationToken cancellationToken)
    {
        var safePage = page < 1 ? 1 : page;
        var safeSize = pageSize is < 1 or > MaxPageSize ? 25 : pageSize;

        var (items, total) = await _installations.ListAsync(safePage, safeSize, storeId, customerId, state, cancellationToken);
        return new InstallationPage(items, safePage, safeSize, total);
    }

    public Task<FeatureInstallation?> GetInstallationAsync(Guid id, CancellationToken cancellationToken) =>
        _installations.GetByIdAsync(id, cancellationToken);

    public async Task<JobPage> ListJobsAsync(
        int page,
        int pageSize,
        Guid? storeId,
        Guid? customerId,
        JobState? state,
        CancellationToken cancellationToken)
    {
        var safePage = page < 1 ? 1 : page;
        var safeSize = pageSize is < 1 or > MaxPageSize ? 25 : pageSize;

        var (items, total) = await _jobs.ListAsync(safePage, safeSize, storeId, customerId, state, cancellationToken);
        return new JobPage(items, safePage, safeSize, total);
    }

    public Task<FeatureInstallationJob?> GetJobAsync(Guid id, CancellationToken cancellationToken) =>
        _jobs.GetByIdAsync(id, cancellationToken);

    public async Task<FeatureInstallationJob> CancelJobAsync(Guid jobId, string reason, CancellationToken cancellationToken)
    {
        var job = await _jobs.GetByIdAsync(jobId, cancellationToken)
            ?? throw new NotFoundException($"Job '{jobId}' was not found.");

        var now = _clock.UtcNow;
        job.Cancel(reason, now);

        // The installation has to come back out of Pending, or the store's queue
        // stays blocked by a job that will never run.
        var installation = await _installations.GetByIdAsync(job.InstallationId, cancellationToken);
        if (installation is not null && installation.CurrentJobId == job.Id)
        {
            installation.MarkFailed(job.Id, "job.cancelled", reason, RollbackOutcome.NotAttempted, now);
        }

        await _jobs.SaveChangesAsync(cancellationToken);
        await _installations.SaveChangesAsync(cancellationToken);

        await _audit.RecordAsync(
            "feature.job.cancelled",
            "FeatureInstallationJob",
            job.Id.ToString(),
            job.CustomerId,
            cancellationToken,
            newValue: new { job.FeatureSlug, Reason = reason });

        return job;
    }

    /// <summary>
    /// Applies an entitlement change across a customer's stores.
    ///
    /// Losing an entitlement disables; it never uninstalls and never deletes.
    /// That is the default policy of docs/feature-delivery.md §11, and it is
    /// applied here rather than left to a caller because the caller is a
    /// subscription expiring at midnight, which is exactly when nobody is
    /// watching.
    /// </summary>
    public async Task ApplyEntitlementChangeAsync(
        Guid customerId,
        Guid featureId,
        bool entitled,
        string reason,
        CancellationToken cancellationToken)
    {
        var installations = await _installations.ListForCustomerFeatureAsync(customerId, featureId, cancellationToken);

        foreach (var installation in installations)
        {
            if (entitled)
            {
                // Regaining an entitlement re-enables what is already there; a
                // store that never had it is handled by the install path, which
                // needs a plan this method has no store context to resolve.
                if (installation.State is InstallationState.Disabled)
                {
                    await QueueSimpleJobAsync(installation, JobType.Enable, reason, cancellationToken);
                }

                continue;
            }

            if (installation.State is InstallationState.Installed)
            {
                await QueueSimpleJobAsync(installation, JobType.Disable, reason, cancellationToken);
            }
        }
    }

    // --- Helpers -----------------------------------------------------------

    /// <summary>
    /// Queues one of the small job types — enable, disable, uninstall, rollback,
    /// configuration — which need no plan because they act on what is already
    /// installed.
    /// </summary>
    private async Task<FeatureInstallationJob> QueueSimpleJobAsync(
        FeatureInstallation installation,
        JobType type,
        string reason,
        CancellationToken cancellationToken,
        bool save = true)
    {
        var now = _clock.UtcNow;

        if (await _jobs.HasUnfinishedJobAsync(installation.StoreId, cancellationToken))
        {
            throw new ConflictException(
                "This store already has an installation job in flight. Only one runs at a time.");
        }

        var job = FeatureInstallationJob.Queue(
            Guid.CreateVersion7(),
            now,
            installation.StoreId,
            installation.CustomerId,
            installation.Id,
            installation.FeatureId,
            installation.FeatureSlug,
            type,
            installation.InstalledVersionId,
            type is JobType.Uninstall ? null : installation.InstalledVersion ?? installation.PreviousVersion,
            $"{type}:{installation.Id}:{now.ToUnixTimeMilliseconds()}",
            CorrelationId(),
            _currentUser.UserId ?? Guid.Empty,
            JobTrigger.Manual,
            traceParent: TraceParent());

        await _jobs.AddAsync(job, cancellationToken);

        if (save)
        {
            await _jobs.SaveChangesAsync(cancellationToken);

            await _audit.RecordAsync(
                $"feature.{type.ToString().ToLowerInvariant()}.requested",
                "FeatureInstallationJob",
                job.Id.ToString(),
                installation.CustomerId,
                cancellationToken,
                newValue: new { installation.FeatureSlug, Reason = reason });
        }

        return job;
    }

    /// <summary>
    /// Refuses to uninstall something another installed Feature depends on.
    ///
    /// Checked against what the store actually has installed rather than against
    /// the registry: the question is not "could anything depend on this" but "does
    /// anything here depend on this", and only the first has an answer that
    /// matters to the store about to lose it.
    /// </summary>
    private async Task EnsureNothingDependsOnAsync(FeatureInstallation installation, CancellationToken cancellationToken)
    {
        var context = await RequireContextAsync(installation.StoreId, cancellationToken);
        var others = context.InstalledFeatures.Keys
            .Where(slug => !string.Equals(slug, installation.FeatureSlug, StringComparison.Ordinal))
            .ToList();

        if (others.Count == 0)
        {
            return;
        }

        var plan = await _resolver.ResolveManyAsync(
            [.. others.Select(slug => (slug, (string?)null))],
            context with { InstalledFeatures = new Dictionary<string, string>(StringComparer.Ordinal) },
            cancellationToken);

        var dependents = plan.Steps
            .Where(step => !step.IsRoot && string.Equals(step.Slug, installation.FeatureSlug, StringComparison.Ordinal))
            .Select(step => step.RequiredBy)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (dependents.Count > 0)
        {
            throw new ConflictException(
                $"'{installation.FeatureSlug}' cannot be uninstalled because {string.Join(", ", dependents)} depends on it. " +
                "Uninstall the dependent features first.");
        }
    }

    private async Task<FeatureInstallation> EnsureInstallationAsync(
        Guid storeId,
        Guid customerId,
        Guid? featureId,
        string slug,
        CancellationToken cancellationToken)
    {
        if (featureId is null)
        {
            throw new NotFoundException($"No feature is registered with slug '{slug}'.");
        }

        var existing = await _installations.FindAsync(storeId, featureId.Value, cancellationToken);
        if (existing is not null)
        {
            return existing;
        }

        var installation = FeatureInstallation.Create(
            Guid.CreateVersion7(),
            _clock.UtcNow,
            storeId,
            customerId,
            featureId.Value,
            slug);

        await _installations.AddAsync(installation, cancellationToken);
        await _installations.SaveChangesAsync(cancellationToken);

        return installation;
    }

    private async Task<FeatureInstallation> RequireInstallationAsync(Guid storeId, Guid featureId, CancellationToken cancellationToken) =>
        await _installations.FindAsync(storeId, featureId, cancellationToken)
        ?? throw new NotFoundException("This store has no record of that feature.");

    private async Task<FeaturePlanContext> RequireContextAsync(Guid storeId, CancellationToken cancellationToken) =>
        await _stores.GetPlanContextAsync(storeId, cancellationToken)
        ?? throw new NotFoundException($"Store '{storeId}' was not found.");

    private async Task<Guid> RequireCustomerAsync(Guid storeId, CancellationToken cancellationToken) =>
        await _stores.GetOwningCustomerAsync(storeId, cancellationToken)
        ?? throw new NotFoundException($"Store '{storeId}' was not found.");

    private static string CorrelationId() => Guid.CreateVersion7().ToString("n");

    /// <summary>
    /// The current request's W3C traceparent, if anything is tracing.
    ///
    /// Read from the ambient activity rather than passed in, because every
    /// caller of this service is either an HTTP request or a background sweep
    /// and both already have one — threading it through every signature would
    /// add a parameter that is never anything but this.
    /// </summary>
    private static string? TraceParent() => System.Diagnostics.Activity.Current?.Id;

    private static bool IsJsonObject(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.ValueKind is JsonValueKind.Object;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
