using FeatureDelivery.Domain;
using Knight.Application.Abstractions.ControlPlane;
using Knight.Application.Abstractions.Identity;
using Knight.Application.Abstractions.Time;
using Knight.Application.Exceptions;
using Microsoft.Extensions.Logging;

namespace FeatureDelivery;

/// <summary>
/// Sequences a Feature version across the fleet. See
/// <see cref="IFeatureRolloutService"/> for why this exists at all.
///
/// The division of labour is the important part: this class decides *which
/// stores go next and whether to carry on*, and <see cref="IFeatureDeliveryService"/>
/// queues the ordinary upgrade job for each one. A rollout cannot ask an agent
/// to do anything a hand-made upgrade could not, which is what keeps the blast
/// radius of a compromised control plane the same as it was before rollouts
/// existed ([`adr/0015`](../../../docs/adr/0015-feature-delivery-mechanism.md)).
/// </summary>
internal sealed class FeatureRolloutService : IFeatureRolloutService
{
    private const int MaxPageSize = 100;

    private readonly IFeatureRolloutRepository _rollouts;
    private readonly IFeatureDeliveryService _delivery;
    private readonly IStoreDeliveryReader _stores;
    private readonly IFeaturePlanResolver _resolver;
    private readonly IAuditTrail _audit;
    private readonly IDateTimeProvider _clock;
    private readonly ICurrentUser _currentUser;
    private readonly ILogger<FeatureRolloutService> _logger;

    public FeatureRolloutService(
        IFeatureRolloutRepository rollouts,
        IFeatureDeliveryService delivery,
        IStoreDeliveryReader stores,
        IFeaturePlanResolver resolver,
        IAuditTrail audit,
        IDateTimeProvider clock,
        ICurrentUser currentUser,
        ILogger<FeatureRolloutService> logger)
    {
        _rollouts = rollouts;
        _delivery = delivery;
        _stores = stores;
        _resolver = resolver;
        _audit = audit;
        _clock = clock;
        _currentUser = currentUser;
        _logger = logger;
    }

    public async Task<FeatureRollout> PlanAsync(PlanRolloutInput input, CancellationToken cancellationToken)
    {
        var now = _clock.UtcNow;

        var candidates = await CandidatesAsync(input, cancellationToken);

        if (candidates.Count == 0)
        {
            throw new ValidationException(new Dictionary<string, string[]>
            {
                ["stores"] = ["No store has this Feature installed on a different version, so there is nothing to roll out."],
            });
        }

        var featureId = candidates[0].FeatureId;
        var versionId = await ResolveVersionIdAsync(candidates[0].StoreId, input, cancellationToken);

        // One live rollout per Feature. Two would race each other onto the same
        // stores, and the loser's jobs would fail against a version the winner
        // had already installed.
        if (await _rollouts.FindActiveForFeatureAsync(featureId, cancellationToken) is { } existing)
        {
            throw new ValidationException(new Dictionary<string, string[]>
            {
                ["slug"] = [$"A rollout of '{input.Slug}' is already {existing.State}. Finish or cancel it first."],
            });
        }

        var ordered = Order(candidates, input.CanaryStoreId);

        var rollout = FeatureRollout.Plan(
            Guid.NewGuid(),
            featureId,
            input.Slug,
            versionId,
            input.Version,
            ordered.Select(store => store.StoreId).ToArray(),
            input.WavePercentages,
            input.FailureThreshold,
            _currentUser.UserId?.ToString() ?? "system",
            now);

        await _rollouts.AddAsync(rollout, cancellationToken);
        await _rollouts.SaveChangesAsync(cancellationToken);

        await _audit.RecordAsync(
            "feature.rollout.planned",
            "FeatureRollout",
            rollout.Id.ToString(),
            null,
            cancellationToken,
            newValue: new
            {
                input.Slug,
                input.Version,
                Stores = rollout.TotalStores,
                Waves = rollout.Waves.Count,
                rollout.FailureThreshold,
            });

        return rollout;
    }

    /// <summary>
    /// Orders the stores so the canary is the safest one available.
    ///
    /// Non-production first, so an unproven version reaches a staging store
    /// before anybody's live shop. This is policy, not a domain rule — a customer
    /// whose only store is production must still be able to roll out, and then
    /// the canary is that store and the operator can see that it is.
    /// </summary>
    private static IReadOnlyList<RolloutCandidateStore> Order(
        IReadOnlyList<RolloutCandidateStore> candidates,
        Guid? canaryStoreId)
    {
        var ordered = candidates
            .OrderBy(store => store.IsProduction ? 1 : 0)
            .ThenBy(store => store.StoreName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (canaryStoreId is not { } wanted)
        {
            return ordered;
        }

        var chosen = ordered.FirstOrDefault(store => store.StoreId == wanted)
            ?? throw new ValidationException(new Dictionary<string, string[]>
            {
                ["canaryStoreId"] = ["That store is not one of this rollout's candidates."],
            });

        ordered.Remove(chosen);
        ordered.Insert(0, chosen);
        return ordered;
    }

    private async Task<IReadOnlyList<RolloutCandidateStore>> CandidatesAsync(
        PlanRolloutInput input,
        CancellationToken cancellationToken)
    {
        var candidates = await _stores.ListRolloutCandidatesAsync(input.Slug, input.Version, cancellationToken);

        if (input.StoreIds is not { Count: > 0 } wanted)
        {
            return candidates.ToArray();
        }

        // An explicit list narrows the candidates; it never widens them. A store
        // that is not a candidate is not one because it lacks the Feature or is
        // not active, and naming it does not change that.
        var allowed = wanted.ToHashSet();
        return candidates.Where(store => allowed.Contains(store.StoreId)).ToArray();
    }

    /// <summary>
    /// Confirms the target version resolves, and answers which version row it is.
    ///
    /// Resolved against a real candidate store rather than in the abstract, so a
    /// version nobody could actually install — incompatible with the store
    /// version, or missing a dependency — is refused while planning rather than
    /// discovered one failed canary later.
    /// </summary>
    private async Task<Guid> ResolveVersionIdAsync(
        Guid sampleStoreId,
        PlanRolloutInput input,
        CancellationToken cancellationToken)
    {
        var context = await _stores.GetPlanContextAsync(sampleStoreId, cancellationToken)
            ?? throw new NotFoundException("The candidate store could not be resolved.");

        var plan = await _resolver.ResolveAsync(input.Slug, input.Version, context, cancellationToken);

        if (!plan.IsSuccessful)
        {
            throw new ValidationException(new Dictionary<string, string[]>
            {
                ["version"] = [$"Version '{input.Version}' of '{input.Slug}' could not be resolved: {plan.DescribeFailures()}"],
            });
        }

        var root = plan.Steps.FirstOrDefault(step => step.IsRoot && step.Slug == input.Slug)
            ?? throw new ValidationException(new Dictionary<string, string[]>
            {
                ["version"] = [$"'{input.Slug}' did not appear in its own resolved plan."],
            });

        return root.VersionId;
    }

    public async Task<FeatureRollout> StartAsync(Guid rolloutId, CancellationToken cancellationToken)
    {
        var rollout = await RequireAsync(rolloutId, cancellationToken);
        var now = _clock.UtcNow;

        rollout.Start(now);
        await _rollouts.SaveChangesAsync(cancellationToken);

        await _audit.RecordAsync("feature.rollout.started", "FeatureRollout", rollout.Id.ToString(), null, cancellationToken);

        return await DispatchNextWaveAsync(rollout, cancellationToken) ?? rollout;
    }

    public async Task<FeatureRollout?> AdvanceAsync(Guid rolloutId, CancellationToken cancellationToken)
    {
        var rollout = await _rollouts.GetByIdAsync(rolloutId, cancellationToken);

        return rollout is null ? null : await DispatchNextWaveAsync(rollout, cancellationToken);
    }

    public async Task<int> AdvanceAllAsync(CancellationToken cancellationToken)
    {
        var dispatched = 0;

        foreach (var rollout in await _rollouts.ListAdvanceableAsync(cancellationToken))
        {
            if (rollout.NextWave() is null)
            {
                continue;
            }

            await DispatchNextWaveAsync(rollout, cancellationToken);
            dispatched++;
        }

        return dispatched;
    }

    /// <summary>
    /// Queues an upgrade job for every store in the next wave, if there is one.
    ///
    /// A store whose job cannot be queued is recorded as failed rather than
    /// skipped. A rollout that quietly passed over stores it could not reach
    /// would report success while leaving them on the old version, which is the
    /// one outcome nobody could act on.
    /// </summary>
    private async Task<FeatureRollout?> DispatchNextWaveAsync(FeatureRollout rollout, CancellationToken cancellationToken)
    {
        if (rollout.NextWave() is not { } wave)
        {
            return rollout;
        }

        var now = _clock.UtcNow;
        rollout.MarkWaveDispatched(wave.Id, now);

        var failures = new List<(Guid StoreId, string Detail)>();

        foreach (var target in wave.Targets)
        {
            try
            {
                var result = await _delivery.UpgradeAsync(
                    new InstallFeatureInput(
                        target.StoreId,
                        rollout.FeatureSlug,
                        rollout.TargetVersion,

                        // Keyed by rollout and store, so a coordinator sweep that
                        // runs twice queues one job rather than two.
                        $"rollout:{rollout.Id}:{target.StoreId}",
                        JobTrigger.Manual),
                    cancellationToken);

                if (result.QueuedJobs.Count > 0)
                {
                    target.AttachJob(result.QueuedJobs[0].Id);
                }
                else
                {
                    failures.Add((target.StoreId, result.Plan.IsSuccessful ? "The upgrade produced no job." : result.Plan.DescribeFailures()));
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                _logger.LogError(
                    exception,
                    "Rollout {RolloutId}: could not queue an upgrade for store {StoreId}.",
                    rollout.Id,
                    target.StoreId);

                failures.Add((target.StoreId, exception.Message));
            }
        }

        foreach (var (storeId, detail) in failures)
        {
            rollout.RecordResult(storeId, succeeded: false, detail, _clock.UtcNow);
        }

        await _rollouts.SaveChangesAsync(cancellationToken);

        await _audit.RecordAsync(
            "feature.rollout.wave.dispatched",
            "FeatureRollout",
            rollout.Id.ToString(),
            null,
            cancellationToken,
            newValue: new
            {
                Wave = wave.Ordinal,
                wave.IsCanary,
                Stores = wave.Targets.Count,
                FailedToQueue = failures.Count,
            });

        return rollout;
    }

    public async Task RecordJobOutcomeAsync(Guid jobId, bool succeeded, string? detail, CancellationToken cancellationToken)
    {
        // Most jobs are not part of a rollout, so this is the common path and it
        // must be cheap and silent.
        var rollout = await _rollouts.FindByJobAsync(jobId, cancellationToken);
        if (rollout is null)
        {
            return;
        }

        var target = rollout.Waves
            .SelectMany(wave => wave.Targets)
            .FirstOrDefault(candidate => candidate.JobId == jobId);

        if (target is null)
        {
            return;
        }

        var halted = rollout.RecordResult(target.StoreId, succeeded, detail, _clock.UtcNow);
        await _rollouts.SaveChangesAsync(cancellationToken);

        if (halted)
        {
            _logger.LogWarning(
                "Rollout {RolloutId} of {Slug} {Version} halted: {Reason}",
                rollout.Id,
                rollout.FeatureSlug,
                rollout.TargetVersion,
                rollout.HaltReason);

            await _audit.RecordAsync(
                "feature.rollout.halted",
                "FeatureRollout",
                rollout.Id.ToString(),
                null,
                cancellationToken,
                newValue: new { rollout.HaltReason, rollout.FailedStores, rollout.SucceededStores });
        }
    }

    public async Task<FeatureRollout> HaltAsync(Guid rolloutId, string reason, CancellationToken cancellationToken)
    {
        var rollout = await RequireAsync(rolloutId, cancellationToken);

        rollout.Halt(reason, _clock.UtcNow);
        await _rollouts.SaveChangesAsync(cancellationToken);

        await _audit.RecordAsync(
            "feature.rollout.halted",
            "FeatureRollout",
            rollout.Id.ToString(),
            null,
            cancellationToken,
            newValue: new { Reason = reason, Manual = true });

        return rollout;
    }

    public async Task<FeatureRollout> ResumeAsync(Guid rolloutId, CancellationToken cancellationToken)
    {
        var rollout = await RequireAsync(rolloutId, cancellationToken);

        rollout.Resume(_clock.UtcNow);
        await _rollouts.SaveChangesAsync(cancellationToken);

        await _audit.RecordAsync(
            "feature.rollout.resumed",
            "FeatureRollout",
            rollout.Id.ToString(),
            null,
            cancellationToken,
            newValue: new { AcceptedFailures = rollout.FailedStores });

        return await DispatchNextWaveAsync(rollout, cancellationToken) ?? rollout;
    }

    public async Task<FeatureRollout> CancelAsync(Guid rolloutId, string reason, CancellationToken cancellationToken)
    {
        var rollout = await RequireAsync(rolloutId, cancellationToken);

        rollout.Cancel(reason, _clock.UtcNow);
        await _rollouts.SaveChangesAsync(cancellationToken);

        await _audit.RecordAsync(
            "feature.rollout.cancelled",
            "FeatureRollout",
            rollout.Id.ToString(),
            null,
            cancellationToken,
            newValue: new { Reason = reason, rollout.SucceededStores, rollout.FailedStores });

        return rollout;
    }

    public Task<FeatureRollout?> GetAsync(Guid rolloutId, CancellationToken cancellationToken) =>
        _rollouts.GetByIdAsync(rolloutId, cancellationToken);

    public async Task<RolloutPage> ListAsync(int page, int pageSize, RolloutState? state, CancellationToken cancellationToken)
    {
        var safePage = Math.Max(1, page);
        var safeSize = Math.Clamp(pageSize, 1, MaxPageSize);

        var (items, total) = await _rollouts.ListAsync(safePage, safeSize, state, cancellationToken);

        return new RolloutPage(items, safePage, safeSize, total);
    }

    private async Task<FeatureRollout> RequireAsync(Guid rolloutId, CancellationToken cancellationToken) =>
        await _rollouts.GetByIdAsync(rolloutId, cancellationToken)
        ?? throw new NotFoundException($"No rollout with id '{rolloutId}'.");
}
