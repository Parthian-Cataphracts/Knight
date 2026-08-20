using Knight.Domain.Common;
using Knight.Domain.Exceptions;

namespace FeatureDelivery.Domain;

/// <summary>
/// A plan to move many stores onto one version of a Feature, a few at a time.
///
/// This exists because of R16 in [`risks.md`](../../../docs/risks.md): KNIGHT
/// delivers executable code into customer production systems, so a bad version
/// can break every store at once. Installing a version store by store, by hand,
/// is not a mitigation — it is the same risk carried out more slowly. A rollout
/// makes the safe order the *only* order:
///
/// - the first wave is a canary, and it is always exactly one store;
/// - a wave does not begin until the wave before it has finished successfully;
/// - failures past a threshold halt the rollout and queue nothing further.
///
/// The rollout does not install anything itself. It decides which stores go
/// next and when to stop; the installation jobs that already exist do the work
/// ([`adr/0015`](../../../docs/adr/0015-feature-delivery-mechanism.md)). Keeping
/// those apart matters: a halted rollout must not cancel an install already
/// running inside a store, because interrupting a migration half-way is worse
/// than letting it finish.
///
/// Waves hold their store list rather than a percentage evaluated later. A
/// rollout that recomputed its targets on each wave would silently change shape
/// when a store was added, removed or suspended mid-rollout, and "which stores
/// did this version actually reach" is the first question asked after an
/// incident.
/// </summary>
public sealed class FeatureRollout : AuditableEntity
{
    private readonly List<RolloutWave> _waves = [];

    private FeatureRollout()
    {
        FeatureSlug = string.Empty;
        TargetVersion = string.Empty;
        CreatedBy = string.Empty;
    }

    public Guid FeatureId { get; private set; }

    public string FeatureSlug { get; private set; }

    public Guid FeatureVersionId { get; private set; }

    public string TargetVersion { get; private set; }

    public RolloutState State { get; private set; }

    /// <summary>
    /// How many failed stores are tolerated across the whole rollout before it
    /// halts. Counted over the rollout rather than per wave: three failures
    /// spread one per wave is the same bad version as three in one wave, and
    /// only the whole-rollout count notices the first shape.
    /// </summary>
    public int FailureThreshold { get; private set; }

    public string CreatedBy { get; private set; }

    public DateTimeOffset? StartedAt { get; private set; }

    public DateTimeOffset? CompletedAt { get; private set; }

    /// <summary>Why a halted rollout halted. Null while it is not halted.</summary>
    public string? HaltReason { get; private set; }

    public IReadOnlyList<RolloutWave> Waves => _waves;

    public int TotalStores => _waves.Sum(wave => wave.Targets.Count);

    public int SucceededStores => _waves.Sum(wave => wave.Targets.Count(target => target.State is RolloutTargetState.Succeeded));

    public int FailedStores => _waves.Sum(wave => wave.Targets.Count(target => target.State is RolloutTargetState.Failed));

    /// <summary>
    /// Plans a rollout over the given stores, canary first.
    ///
    /// <paramref name="orderedStoreIds"/> is taken in the order given and the
    /// caller decides it. The service puts staging stores before production ones
    /// so the canary is never a production store when a non-production one
    /// exists, and that is a policy decision rather than a domain rule — a
    /// customer with only production stores must still be able to roll out.
    /// </summary>
    public static FeatureRollout Plan(
        Guid id,
        Guid featureId,
        string featureSlug,
        Guid featureVersionId,
        string targetVersion,
        IReadOnlyList<Guid> orderedStoreIds,
        IReadOnlyList<int> wavePercentages,
        int failureThreshold,
        string createdBy,
        DateTimeOffset now)
    {
        if (orderedStoreIds.Count == 0)
        {
            throw DomainException.Validation("A rollout needs at least one store to roll out to.");
        }

        if (orderedStoreIds.Distinct().Count() != orderedStoreIds.Count)
        {
            // A store in two waves would be installed onto twice, and the second
            // job would find the version already there and fail confusingly.
            throw DomainException.Validation("A store may appear in a rollout only once.");
        }

        if (failureThreshold < 1)
        {
            throw DomainException.Validation("The failure threshold must be at least one.");
        }

        var rollout = new FeatureRollout
        {
            Id = id,
            FeatureId = featureId,
            FeatureSlug = featureSlug,
            FeatureVersionId = featureVersionId,
            TargetVersion = targetVersion,
            State = RolloutState.Planned,
            FailureThreshold = failureThreshold,
            CreatedBy = createdBy,
            CreatedAt = now,
        };

        foreach (var (storeIds, index) in Partition(orderedStoreIds, wavePercentages).Select((batch, i) => (batch, i)))
        {
            rollout._waves.Add(RolloutWave.Create(Guid.NewGuid(), id, index, storeIds, now));
        }

        return rollout;
    }

    /// <summary>
    /// Splits the stores into waves: one canary, then the requested percentages.
    ///
    /// The canary is one store, never a percentage of them. "Five percent" of
    /// four hundred stores is twenty, and twenty broken stores is not a canary —
    /// it is an incident that a smaller first wave would have caught.
    /// </summary>
    private static List<List<Guid>> Partition(IReadOnlyList<Guid> stores, IReadOnlyList<int> percentages)
    {
        var waves = new List<List<Guid>> { new() { stores[0] } };

        var remaining = stores.Skip(1).ToList();
        if (remaining.Count == 0)
        {
            return waves;
        }

        var steps = percentages.Count > 0 ? percentages : new[] { 100 };
        var taken = 0;

        for (var i = 0; i < steps.Count && taken < remaining.Count; i++)
        {
            // The last wave always takes everything left, whatever the
            // percentages add up to. Percentages that sum to 90 must not leave
            // ten percent of stores silently un-upgraded.
            var isLast = i == steps.Count - 1;
            var size = isLast
                ? remaining.Count - taken
                : Math.Max(1, (int)Math.Floor(remaining.Count * (steps[i] / 100.0)));

            size = Math.Min(size, remaining.Count - taken);
            if (size <= 0)
            {
                continue;
            }

            waves.Add(remaining.Skip(taken).Take(size).ToList());
            taken += size;
        }

        if (taken < remaining.Count)
        {
            waves.Add(remaining.Skip(taken).ToList());
        }

        return waves;
    }

    /// <summary>
    /// The wave that should be dispatched next, or null when there is nothing to
    /// dispatch *yet*.
    ///
    /// Null while a wave is still in flight, which is the rule that makes this a
    /// staged rollout rather than a slower way of installing everywhere at once:
    /// the next wave is not offered until the one before it has reported on every
    /// store, and a wave that reported a failure halts the rollout before this is
    /// asked again.
    /// </summary>
    public RolloutWave? NextWave()
    {
        if (State is not (RolloutState.InProgress or RolloutState.Planned))
        {
            return null;
        }

        if (_waves.Any(wave => wave.State is RolloutWaveState.Dispatched))
        {
            return null;
        }

        return _waves.FirstOrDefault(wave => wave.State is RolloutWaveState.Pending);
    }

    public RolloutWave? CurrentWave() => _waves.FirstOrDefault(wave => wave.State is RolloutWaveState.Dispatched);

    public void Start(DateTimeOffset now)
    {
        if (State is not RolloutState.Planned)
        {
            throw DomainException.Validation($"A rollout in state '{State}' cannot be started.");
        }

        State = RolloutState.InProgress;
        StartedAt = now;
        MarkUpdated(now);
    }

    /// <summary>Records that a wave's jobs have been queued.</summary>
    public void MarkWaveDispatched(Guid waveId, DateTimeOffset now)
    {
        Wave(waveId).Dispatch(now);
        MarkUpdated(now);
    }

    /// <summary>
    /// Records what one store's job did.
    ///
    /// Returns whether the rollout halted as a result, so the caller can say so
    /// rather than having to re-read the state and infer it.
    /// </summary>
    public bool RecordResult(Guid storeId, bool succeeded, string? detail, DateTimeOffset now)
    {
        var wave = _waves.FirstOrDefault(candidate => candidate.Targets.Any(target => target.StoreId == storeId))
            ?? throw DomainException.Validation("That store is not part of this rollout.");

        wave.RecordResult(storeId, succeeded, detail, now);
        MarkUpdated(now);

        if (State is RolloutState.Halted)
        {
            return false;
        }

        // A canary that failed halts the rollout whatever the threshold says. The
        // threshold expresses how many failures are tolerable across a fleet;
        // it is not permission to carry on past the one store that exists
        // precisely to be broken first. Everything behind the canary is still
        // untouched at this point, which is the cheapest moment there will ever
        // be to stop.
        if (wave.IsCanary && !succeeded)
        {
            Halt($"The canary store failed: {detail ?? "no detail given"}.", now);
            return true;
        }

        if (FailedStores >= FailureThreshold)
        {
            // Halting rather than failing: the stores already upgraded are still
            // upgraded, and somebody has to decide whether to roll them back.
            Halt($"{FailedStores} store(s) failed, which meets the threshold of {FailureThreshold}.", now);
            return true;
        }

        if (_waves.All(candidate => candidate.State is RolloutWaveState.Completed))
        {
            State = RolloutState.Completed;
            CompletedAt = now;
        }

        return false;
    }

    public void Halt(string reason, DateTimeOffset now)
    {
        if (State is RolloutState.Completed or RolloutState.Cancelled)
        {
            throw DomainException.Validation($"A rollout in state '{State}' cannot be halted.");
        }

        State = RolloutState.Halted;
        HaltReason = reason;
        MarkUpdated(now);
    }

    /// <summary>
    /// Resumes a halted rollout, deliberately without clearing the failures that
    /// halted it.
    ///
    /// The threshold is raised to just past the current failure count instead, so
    /// resuming means "I have looked at those failures and accept them", not
    /// "pretend they did not happen". The next failure still halts it.
    /// </summary>
    public void Resume(DateTimeOffset now)
    {
        if (State is not RolloutState.Halted)
        {
            throw DomainException.Validation($"A rollout in state '{State}' cannot be resumed.");
        }

        FailureThreshold = FailedStores + 1;
        State = RolloutState.InProgress;
        HaltReason = null;
        MarkUpdated(now);
    }

    /// <summary>
    /// Stops the rollout for good. Stores already upgraded stay upgraded — a
    /// rollout is not a transaction, and pretending it is would mean
    /// automatically downgrading production stores that are working.
    /// </summary>
    public void Cancel(string reason, DateTimeOffset now)
    {
        if (State is RolloutState.Completed)
        {
            throw DomainException.Validation("A completed rollout cannot be cancelled.");
        }

        State = RolloutState.Cancelled;
        HaltReason = reason;
        CompletedAt = now;
        MarkUpdated(now);
    }

    private RolloutWave Wave(Guid waveId) =>
        _waves.FirstOrDefault(wave => wave.Id == waveId)
        ?? throw DomainException.Validation("That wave does not belong to this rollout.");
}

/// <summary>
/// One batch of stores within a rollout. The first wave is always the canary and
/// always holds exactly one store.
/// </summary>
public sealed class RolloutWave : Entity
{
    private readonly List<RolloutTarget> _targets = [];

    private RolloutWave()
    {
    }

    public Guid RolloutId { get; private set; }

    /// <summary>Zero-based. Wave 0 is the canary.</summary>
    public int Ordinal { get; private set; }

    public RolloutWaveState State { get; private set; }

    public DateTimeOffset? DispatchedAt { get; private set; }

    public DateTimeOffset? CompletedAt { get; private set; }

    public IReadOnlyList<RolloutTarget> Targets => _targets;

    public bool IsCanary => Ordinal == 0;

    internal static RolloutWave Create(Guid id, Guid rolloutId, int ordinal, IReadOnlyList<Guid> storeIds, DateTimeOffset now)
    {
        var wave = new RolloutWave
        {
            Id = id,
            RolloutId = rolloutId,
            Ordinal = ordinal,
            State = RolloutWaveState.Pending,
        };

        foreach (var storeId in storeIds)
        {
            wave._targets.Add(RolloutTarget.Create(Guid.NewGuid(), id, storeId, now));
        }

        return wave;
    }

    internal void Dispatch(DateTimeOffset now)
    {
        if (State is not RolloutWaveState.Pending)
        {
            throw DomainException.Validation($"A wave in state '{State}' cannot be dispatched.");
        }

        State = RolloutWaveState.Dispatched;
        DispatchedAt = now;

        foreach (var target in _targets)
        {
            target.MarkDispatched(now);
        }
    }

    internal void RecordResult(Guid storeId, bool succeeded, string? detail, DateTimeOffset now)
    {
        var target = _targets.FirstOrDefault(candidate => candidate.StoreId == storeId)
            ?? throw DomainException.Validation("That store is not part of this wave.");

        target.Record(succeeded, detail, now);

        // A wave is complete when nothing in it is still outstanding, whether or
        // not everything succeeded. Whether the *rollout* may continue is the
        // rollout's decision, not the wave's.
        if (_targets.All(candidate => candidate.State is RolloutTargetState.Succeeded or RolloutTargetState.Failed))
        {
            State = RolloutWaveState.Completed;
            CompletedAt = now;
        }
    }

    /// <summary>True when every store in the wave finished and none of them failed.</summary>
    public bool SucceededCleanly =>
        State is RolloutWaveState.Completed && _targets.All(target => target.State is RolloutTargetState.Succeeded);
}

/// <summary>One store's place in a rollout, and what became of it.</summary>
public sealed class RolloutTarget : Entity
{
    private RolloutTarget()
    {
    }

    public Guid WaveId { get; private set; }

    public Guid StoreId { get; private set; }

    public RolloutTargetState State { get; private set; }

    /// <summary>The installation job queued for this store, once there is one.</summary>
    public Guid? JobId { get; private set; }

    public string? Detail { get; private set; }

    public DateTimeOffset? DispatchedAt { get; private set; }

    public DateTimeOffset? CompletedAt { get; private set; }

    internal static RolloutTarget Create(Guid id, Guid waveId, Guid storeId, DateTimeOffset now) =>
        new()
        {
            Id = id,
            WaveId = waveId,
            StoreId = storeId,
            State = RolloutTargetState.Pending,
        };

    internal void MarkDispatched(DateTimeOffset now)
    {
        State = RolloutTargetState.Dispatched;
        DispatchedAt = now;
    }

    /// <summary>Records the job this target is waiting on.</summary>
    public void AttachJob(Guid jobId)
    {
        JobId = jobId;
    }

    internal void Record(bool succeeded, string? detail, DateTimeOffset now)
    {
        State = succeeded ? RolloutTargetState.Succeeded : RolloutTargetState.Failed;
        Detail = detail;
        CompletedAt = now;
    }
}

public enum RolloutState
{
    Planned = 0,
    InProgress = 1,

    /// <summary>Stopped because too many stores failed. Resumable once somebody has looked.</summary>
    Halted = 2,
    Completed = 3,
    Cancelled = 4,
}

public enum RolloutWaveState
{
    Pending = 0,
    Dispatched = 1,
    Completed = 2,
}

public enum RolloutTargetState
{
    Pending = 0,
    Dispatched = 1,
    Succeeded = 2,
    Failed = 3,
}
